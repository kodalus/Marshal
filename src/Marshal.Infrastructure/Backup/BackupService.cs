using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Sync;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Backup;

/// <summary>
/// Kopia zapasowa: wszystko do jednego pliku JSON, bez konta i bez sieci (spec 12).
/// </summary>
/// <remarks>
/// <para>
/// Osobno od synchronizacji, a nie zamiast niej. Synchronizacja chroni przed utratą
/// urządzenia; kopia chroni przed <b>utratą Dysku, konta albo zaufania do nich</b> —
/// i przed przypadkiem, w którym błąd rozjechał dane i rozsiał je na oba urządzenia.
/// Dlatego plik leży tam, gdzie go położysz, i otwiera się bez logowania.
/// </para>
/// <para>
/// Co się eksportuje: **agregaty synchronizowane**, rozpoznawane po tym samym warunku
/// co przy scalaniu — dziedziczą po <see cref="Entity"/>. Rzeczy lokalne (kursory,
/// pokazane przypomnienia, pobrane wydarzenia kalendarza, dziennik zmian) nie wchodzą:
/// należą do urządzenia, a nie do danych, i na nowym sprzęcie odtworzą się same.
/// Treść załączników też nie — to pliki, nie tekst; w kopii jest wpis, nie zawartość.
/// </para>
/// </remarks>
public sealed class BackupService(MarshalDbContext db, IHlcSource hlc, IClock clock, IDeviceIdentity device)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // Kopia jest do oglądania, nie do przesyłania. Wcięcia kosztują miejsce
        // i nic poza tym, a plik, którego nie da się przeczytać notatnikiem,
        // jest obietnicą, nie zabezpieczeniem.
        WriteIndented = true,
    };

    public async Task ExportAsync(Stream destination, CancellationToken ct = default)
    {
        var plik = new BackupFile
        {
            CreatedAt = clock.Now,
            DeviceId = device.Id,
            Lines = await BuildLinesAsync(ct),
        };

        await JsonSerializer.SerializeAsync(destination, plik, Json, ct);
    }

    public async Task<ImportReport> ImportAsync(
        Stream source, ImportMode mode, CancellationToken ct = default)
    {
        var plik = await JsonSerializer.DeserializeAsync<BackupFile>(source, Json, ct)
            ?? throw new InvalidDataException("Plik nie wygląda na kopię zapasową Marshala.");

        // Wgranie kopii nie jest zmianą tego urządzenia: bez tego każdy odtworzony
        // rekord wróciłby do dziennika i poleciał na Dysk jako świeża zmiana,
        // wskrzeszając na drugim urządzeniu rzeczy skasowane po zrobieniu kopii.
        using var zakres = SyncScope.Begin();

        // Wszystko albo nic. Podmiana całości czyści tabele **przed** wgraniem, więc
        // bez transakcji błąd w połowie zostawiłby bazę pustą i nieodtworzoną —
        // odtwarzanie kopii jest ostatnią rzeczą, która ma prawo kasować dane.
        await using var transakcja = await db.Database.BeginTransactionAsync(ct);

        try
        {
            if (mode == ImportMode.Replace)
            {
                // Pusty plik przy podmianie to prawie na pewno pomyłka, a podmiana
                // jest jedyną operacją w aplikacji, która kasuje dane naprawdę.
                // Kto chce zacząć od zera, odinstalowuje aplikację.
                if (plik.Lines.Count == 0)
                {
                    throw new InvalidDataException(
                        "Kopia nie zawiera żadnych wpisów — podmiana wyczyściłaby bazę i nie wgrała nic.");
                }

                await ClearAsync(ct);
            }

            var applier = new ChangeApplier(db, hlc);
            var nalozone = 0;

            foreach (var wiersz in plik.Lines)
            {
                if (applier.Apply(wiersz))
                {
                    nalozone++;
                }
            }

            // Nierozpoznane tabele — plik z innej wersji albo z innej aplikacji —
            // scalanie pomija po cichu i słusznie. Przy podmianie to samo pominięcie
            // znaczy bazę wyczyszczoną i nieodtworzoną, więc tu musi być błędem.
            if (mode == ImportMode.Replace && nalozone == 0)
            {
                throw new InvalidDataException(
                    $"Z {plik.Lines.Count} wpisów nie dało się wczytać żadnego — to nie wygląda na kopię Marshala.");
            }

            // Scalanie podnosi zegar lokalny ponad znaczniki z pliku — tak samo jak
            // przy synchronizacji, i z tego samego powodu (spec 9.6).
            LastHlcStore.Stage(db, hlc.Last);

            await db.SaveChangesAsync(ct);
            await transakcja.CommitAsync(ct);

            return new ImportReport(plik.Lines.Count, nalozone, plik.Lines.Count - nalozone);
        }
        catch
        {
            // Wycofanie cofa bazę, ale nie śledzenie zmian: zostałyby w nim obiekty,
            // których w bazie już nie ma, i następny zapis próbowałby je dodać.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Bieżący stan jako wiersze zmian — **pogrupowane po znaczniku pola**, nie po encji.
    /// </summary>
    /// <remarks>
    /// Gdyby cała encja szła pod jednym znacznikiem (tym z <c>UpdatedAt</c>), pola
    /// zmienione dawno dostałyby w kopii datę ostatniej zmiany czegokolwiek w tym
    /// rekordzie — i po wgraniu wygrałyby ze świeższymi wartościami z drugiego
    /// urządzenia. Kopia cofałaby wtedy dane, wyglądając na poprawną. Znacznik pola
    /// jest tym, co o polu wiemy, więc to on jedzie w pliku.
    /// </remarks>
    private async Task<List<ChangeLine>> BuildLinesAsync(CancellationToken ct)
    {
        var wiersze = new List<ChangeLine>();

        foreach (var typ in db.Model.GetEntityTypes()
            .Where(t => typeof(Entity).IsAssignableFrom(t.ClrType))
            .OrderBy(t => t.GetTableName(), StringComparer.Ordinal))
        {
            var tabela = typ.GetTableName()!;

            var znaczniki = (await db.FieldStamps
                    .Where(s => s.EntityType == tabela)
                    .ToListAsync(ct))
                .ToDictionary(s => (s.EntityId, s.Field), s => s.Hlc);

            var wlasciwosci = typ.GetProperties().Where(p => !p.IsPrimaryKey()).ToList();

            foreach (var encja in await RowsAsync(typ.ClrType, ct))
            {
                var wpis = db.Entry(encja);

                // Pole bez znacznika to pole zapisane, zanim znaczniki istniały —
                // albo ustawione konstruktorem. Znacznik encji jest wtedy jedynym,
                // co o nim wiadomo, i jest prawdziwy: rekord na pewno nie zmienił się
                // później niż jego własne UpdatedAt.
                var domyslny = encja.UpdatedAt.ToString();

                foreach (var grupa in wlasciwosci
                    .Select(p => (
                        Pole: p.Name,
                        Hlc: znaczniki.GetValueOrDefault((encja.Id, p.Name)),
                        Wartosc: Encode(wpis, p.Name)))

                    // Pole bez znacznika i bez wartości nigdy nie było ustawione —
                    // dziennik pomija puste przy zakładaniu rekordu. Wypisane w kopii
                    // byłoby jawnym „wyczyść to", opatrzonym znacznikiem całej encji,
                    // i potrafiłoby skasować wartość nadaną w międzyczasie na drugim
                    // urządzeniu. Brak wiedzy o polu nie jest wiedzą, że jest puste.
                    .Where(x => x.Hlc is not null || x.Wartosc is not null)
                    .Select(x => (x.Pole, Hlc: x.Hlc ?? domyslny, x.Wartosc))
                    .GroupBy(x => x.Hlc)
                    .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    wiersze.Add(new ChangeLine
                    {
                        Entity = tabela,
                        Id = encja.Id.ToString(),
                        Hlc = grupa.Key,
                        Fields = grupa.ToDictionary(x => x.Pole, x => x.Wartosc),
                    });
                }
            }
        }

        return wiersze;
    }

    /// <summary>
    /// Wiersze tabeli agregatu, po typie rozstrzygniętym dopiero w czasie działania.
    /// </summary>
    /// <remarks>
    /// Przez model, nie przez wyliczoną listę zbiorów. Lista wymagałaby dopisania
    /// każdego nowego agregatu także tutaj, a zapomnienie nie dałoby żadnego objawu
    /// poza kopią, w której po prostu czegoś nie ma — czyli objawem wychodzącym
    /// dopiero przy odtwarzaniu. Strażnikiem jest test, który porównuje zawartość
    /// kopii z modelem.
    /// </remarks>
    private async Task<List<Entity>> RowsAsync(Type clrType, CancellationToken ct) =>
        await (Task<List<Entity>>)GetType()
            .GetMethod(nameof(RowsOfAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(clrType)
            .Invoke(this, [ct])!;

    private async Task<List<Entity>> RowsOfAsync<T>(CancellationToken ct)
        where T : Entity =>
        await db.Set<T>().Cast<Entity>().ToListAsync(ct);

    /// <summary>
    /// Wyczyszczenie agregatów synchronizowanych razem z ich znacznikami pól.
    /// </summary>
    /// <remarks>
    /// Znaczniki muszą zniknąć razem z danymi, inaczej podmiana nie byłaby podmianą:
    /// zostawione znaczniki są **nowsze albo równe** wszystkiemu, co jest w pliku,
    /// więc odrzuciłyby wgrywane wartości i baza zostałaby pusta.
    /// </remarks>
    private async Task ClearAsync(CancellationToken ct)
    {
        foreach (var typ in db.Model.GetEntityTypes()
            .Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
        {
            var tabela = typ.GetTableName()!;

            // Nazwa tabeli sklejana poza wywołaniem: przekazany wprost tekst z wstawką
            // trafiłby na przeciążenie dla łańcuchów formatowalnych i analizator
            // słusznie zgłosiłby wstrzykiwanie SQL. Źródłem jest tu model EF,
            // nie cokolwiek wpisanego przez człowieka.
            var czyszczenie = "DELETE FROM \"" + tabela + "\"";

            await db.Database.ExecuteSqlRawAsync(czyszczenie, ct);
            await db.FieldStamps.Where(s => s.EntityType == tabela).ExecuteDeleteAsync(ct);
        }

        // Śledzenie zmian trzyma obiekty, których w bazie już nie ma. Pozostawione
        // sprawiłyby, że Find zwróci rekord sprzed czyszczenia i nałożenie uzna,
        // że nie ma czego tworzyć.
        db.ChangeTracker.Clear();
    }

    private static JsonNode? Encode(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry wpis, string pole)
    {
        var wlasciwosc = wpis.Property(pole);
        var wartosc = wlasciwosc.CurrentValue;

        if (wartosc is null)
        {
            return null;
        }

        // Postać bazodanowa, po konwerterze — dokładnie ta, którą niesie dziennik
        // zmian, żeby wgrywanie kopii i scalanie czytały to samo.
        var konwerter = wlasciwosc.Metadata.GetValueConverter();
        var zapisana = konwerter is null ? wartosc : konwerter.ConvertToProvider(wartosc);

        return zapisana is null ? null : JsonSerializer.SerializeToNode(zapisana, Json);
    }
}
