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
public sealed class BackupService(
    MarshalDbContext db,
    IHlcSource hlc,
    IClock clock,
    IDeviceIdentity device,
    IDbQueue? queue = null)
{
    // Kopia czyta albo podmienia **całą** bazę, więc idzie przez bramę w całości,
    // a nie zapytaniami. Synchronizacja wchodząca w środek odtwarzania zapisałaby
    // na Dysk stan z połowy podmiany.
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

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
        var file = new BackupFile
        {
            CreatedAt = clock.Now,
            DeviceId = device.Id,
            Lines = await _queue.RunAsync(() => BuildLinesAsync(ct), ct),
        };

        await JsonSerializer.SerializeAsync(destination, file, Json, ct);
    }

    public async Task<ImportReport> ImportAsync(
        Stream source, ImportMode mode, CancellationToken ct = default)
    {
        // Czytanie pliku poza bramą: to strumień, nie baza, a bywa duży.
        var file = await JsonSerializer.DeserializeAsync<BackupFile>(source, Json, ct)
            ?? throw new InvalidDataException("Plik nie wygląda na kopię zapasową Marshala.");

        return await _queue.RunAsync(() => UploadAsync(file, mode, ct), ct);
    }

    private async Task<ImportReport> UploadAsync(
        BackupFile file, ImportMode mode, CancellationToken ct)
    {
        // Wgranie kopii nie jest zmianą tego urządzenia: bez tego każdy odtworzony
        // rekord wróciłby do dziennika i poleciał na Dysk jako świeża zmiana,
        // wskrzeszając na drugim urządzeniu rzeczy skasowane po zrobieniu kopii.
        using var scope = SyncScope.Begin();

        // Wszystko albo nic. Podmiana całości czyści tabele **przed** wgraniem, więc
        // bez transakcji błąd w połowie zostawiłby bazę pustą i nieodtworzoną —
        // odtwarzanie kopii jest ostatnią rzeczą, która ma prawo kasować dane.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            if (mode == ImportMode.Replace)
            {
                // Pusty plik przy podmianie to prawie na pewno pomyłka, a podmiana
                // jest jedyną operacją w aplikacji, która kasuje dane naprawdę.
                // Kto chce zacząć od zera, odinstalowuje aplikację.
                if (file.Lines.Count == 0)
                {
                    throw new InvalidDataException(
                        "Kopia nie zawiera żadnych wpisów — podmiana wyczyściłaby bazę i nie wgrała nic.");
                }

                await ClearAsync(ct);
            }

            var applier = new ChangeApplier(db, hlc);
            var applied = 0;

            foreach (var row in file.Lines)
            {
                if (applier.Apply(row))
                {
                    applied++;
                }
            }

            // Nierozpoznane tabele — plik z innej wersji albo z innej aplikacji —
            // scalanie pomija po cichu i słusznie. Przy podmianie to samo pominięcie
            // znaczy bazę wyczyszczoną i nieodtworzoną, więc tu musi być błędem.
            if (mode == ImportMode.Replace && applied == 0)
            {
                throw new InvalidDataException(
                    $"Z {file.Lines.Count} wpisów nie dało się wczytać żadnego — to nie wygląda na kopię Marshala.");
            }

            // Scalanie podnosi zegar lokalny ponad znaczniki z pliku — tak samo jak
            // przy synchronizacji, i z tego samego powodu (spec 9.6).
            LastHlcStore.Stage(db, hlc.Last);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new ImportReport(file.Lines.Count, applied, file.Lines.Count - applied);
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
        var rows = new List<ChangeLine>();

        foreach (var typ in db.Model.GetEntityTypes()
            .Where(t => typeof(Entity).IsAssignableFrom(t.ClrType))
            .OrderBy(t => t.GetTableName(), StringComparer.Ordinal))
        {
            var table = typ.GetTableName()!;

            var stamps = (await db.FieldStamps
                    .Where(s => s.EntityType == table)
                    .ToListAsync(ct))
                .ToDictionary(s => (s.EntityId, s.Field), s => s.Hlc);

            var properties = typ.GetProperties().Where(p => !p.IsPrimaryKey()).ToList();

            foreach (var entity in await RowsAsync(typ.ClrType, ct))
            {
                var entry = db.Entry(entity);

                // Pole bez znacznika to pole zapisane, zanim znaczniki istniały —
                // albo ustawione konstruktorem. Znacznik encji jest wtedy jedynym,
                // co o nim wiadomo, i jest prawdziwy: rekord na pewno nie zmienił się
                // później niż jego własne UpdatedAt.
                var default = entity.UpdatedAt.ToString();

                foreach (var group in properties
                    .Select(p => (
                        Pole: p.Name,
                        Hlc: stamps.GetValueOrDefault((entity.Id, p.Name)),
                        Value: Encode(entry, p.Name)))

                    // Pole bez znacznika i bez wartości nigdy nie było ustawione —
                    // dziennik pomija puste przy zakładaniu rekordu. Wypisane w kopii
                    // byłoby jawnym „wyczyść to", opatrzonym znacznikiem całej encji,
                    // i potrafiłoby skasować wartość nadaną w międzyczasie na drugim
                    // urządzeniu. Brak wiedzy o polu nie jest wiedzą, że jest puste.
                    .Where(x => x.Hlc is not null || x.Value is not null)
                    .Select(x => (x.Pole, Hlc: x.Hlc ?? default, x.Value))
                    .GroupBy(x => x.Hlc)
                    .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    rows.Add(new ChangeLine
                    {
                        Entity = table,
                        Id = entity.Id.ToString(),
                        Hlc = group.Key,
                        Fields = group.ToDictionary(x => x.Pole, x => x.Value),
                    });
                }
            }
        }

        return rows;
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
            var table = typ.GetTableName()!;

            // Nazwa tabeli sklejana poza wywołaniem: przekazany wprost tekst z wstawką
            // trafiłby na przeciążenie dla łańcuchów formatowalnych i analizator
            // słusznie zgłosiłby wstrzykiwanie SQL. Źródłem jest tu model EF,
            // nie cokolwiek wpisanego przez człowieka.
            var cleanup = "DELETE FROM \"" + table + "\"";

            await db.Database.ExecuteSqlRawAsync(cleanup, ct);
            await db.FieldStamps.Where(s => s.EntityType == table).ExecuteDeleteAsync(ct);
        }

        // Śledzenie zmian trzyma obiekty, których w bazie już nie ma. Pozostawione
        // sprawiłyby, że Find zwróci rekord sprzed czyszczenia i nałożenie uzna,
        // że nie ma czego tworzyć.
        db.ChangeTracker.Clear();
    }

    private static JsonNode? Encode(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string pole)
    {
        var property = entry.Property(pole);
        var value = property.CurrentValue;

        if (value is null)
        {
            return null;
        }

        // Postać bazodanowa, po konwerterze — dokładnie ta, którą niesie dziennik
        // zmian, żeby wgrywanie kopii i scalanie czytały to samo.
        var converter = property.Metadata.GetValueConverter();
        var saved = converter is null ? value : converter.ConvertToProvider(value);

        return saved is null ? null : JsonSerializer.SerializeToNode(saved, Json);
    }
}
