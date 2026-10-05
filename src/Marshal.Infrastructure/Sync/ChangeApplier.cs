using System.Text.Json;
using System.Text.Json.Nodes;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Sync;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Marshal.Infrastructure.Sync;

/// <summary>
/// Nałożenie wiersza zmian na bazę: scalanie **per pole** po zegarze logicznym (spec 9.4).
/// </summary>
/// <remarks>
/// Osobna klasa, a nie metoda synchronizacji, bo tą samą drogą wchodzi kopia zapasowa
/// (spec 12). Dwie implementacje reguły „nowsze pole wygrywa" rozjechałyby się przy
/// pierwszej zmianie modelu — po cichu i tylko u tego, kto akurat odtwarzał kopię.
///
/// Wołający odpowiada za <see cref="SyncScope"/> i za zapis: nałożenie samo z siebie
/// niczego nie utrwala, bo i synchronizacja, i wgrywanie kopii chcą to zrobić raz,
/// na końcu, razem z przesunięciem kursorów.
/// </remarks>
internal sealed class ChangeApplier(MarshalDbContext db, IHlcSource hlc)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Czy wiersz cokolwiek zmienił. Fałsz znaczy „przegrał" albo „nieznany".</summary>
    public bool Apply(ChangeLine row)
    {
        var typ = db.Model.GetEntityTypes()
            .FirstOrDefault(t => t.GetTableName() == row.Entity);

        // Nieznana tabela albo tabela lokalna: wpis z nowszej wersji aplikacji.
        // Pomijamy, zamiast przerywać — reszta pliku może być zrozumiała.
        if (typ is null || !typeof(Entity).IsAssignableFrom(typ.ClrType))
        {
            return false;
        }

        if (!Guid.TryParse(row.Id, out var id) || !Hlc.TryParse(row.Hlc, out var remote))
        {
            return false;
        }

        // Podniesienie zegara lokalnego ponad wszystko, co widzieliśmy, żeby kolejna
        // zmiana na tym urządzeniu była późniejsza od zdalnej (spec 3.5).
        hlc.Observe(remote);

        var entity = db.Find(typ.ClrType, id);

        if (entity is null)
        {
            entity = Activator.CreateInstance(typ.ClrType, nonPublic: true)
                ?? throw new InvalidOperationException($"Nie da się utworzyć {typ.ClrType}.");

            db.Add(entity);
            db.Entry(entity).Property("Id").CurrentValue = id;
        }

        var entry = db.Entry(entity);
        var anything = false;

        foreach (var (pole, value) in row.Fields)
        {
            var property = typ.FindProperty(pole);

            if (property is null || property.IsPrimaryKey())
            {
                continue;
            }

            if (!Newer(row.Entity, id, pole, remote))
            {
                continue;
            }

            try
            {
                entry.Property(pole).CurrentValue = Decode(value, property);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException
                                      or InvalidCastException or FormatException
                                      or ArgumentException)
            {
                // Wartość, której nie da się wczytać w typ kolumny: plik ucięty,
                // uszkodzony albo z wersji, która trzymała to pole inaczej. Pomijamy
                // **samo pole**, a nie cały wiersz i nie cały przebieg — tak samo jak
                // przy nieczytelnej linii (zob. ChangeLine.TryParse). Przerwanie
                // znaczyłoby, że jedna zła wartość blokuje wszystko, co przyszło po niej,
                // a przy wgrywaniu kopii — że odtwarzanie wywraca się w połowie.
                continue;
            }

            Stamp(row.Entity, id, pole, row.Hlc);
            anything = true;
        }

        return anything;
    }

    /// <summary>
    /// Czy zdalna zmiana jest nowsza od tego, co mamy w tym **polu**.
    /// </summary>
    /// <remarks>
    /// Porównanie per pole, nie per encja. Gdyby porównywać znacznik encji,
    /// zmiana tytułu na jednym urządzeniu unieważniałaby zmianę wagi na drugim,
    /// mimo że dotyczą różnych rzeczy i obie są poprawne.
    /// </remarks>
    private bool Newer(string table, Guid id, string pole, Hlc remote)
    {
        var local = Stamp(table, id, pole);
        return local is null || remote > Hlc.Parse(local.Hlc);
    }

    private void Stamp(string table, Guid id, string pole, string stamp)
    {
        if (Stamp(table, id, pole) is { } existing)
        {
            existing.Update(stamp);
        }
        else
        {
            var fresh = new FieldStamp(table, id, pole, stamp);
            db.Add(fresh);
            _tracked[(table, id, pole)] = fresh;
        }
    }

    /// <summary>
    /// Znaczniki dopisane w tym przebiegu — w mapie, nie do przeszukania po kolei.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tu siedziało zatkanie, przez które aplikacja stawała na minuty. Szukanie znacznika
    /// przechodziło <b>całe śledzenie zmian kontekstu</b> — a kontekst jest w tej
    /// aplikacji jeden na cały proces i żyje tak długo, jak ona. Po przeniesieniu rytmów
    /// i postawieniu okien serii śledzonych znaczników były dziesiątki tysięcy, a to
    /// przeszukanie wykonywało się <b>raz na każde pole każdego nakładanego wiersza</b>.
    /// Scalanie rosło więc z kwadratem tego, ile już się nałożyło — stąd jedno wejście
    /// do bramy trzymające ją sto sześćdziesiąt siedem sekund.
    /// </para>
    /// <para>
    /// Mapa buduje się raz, przy pierwszym pytaniu, i jest dalej utrzymywana
    /// dopisywaniem. Tę samą sztuczkę — jedno zapytanie na cały zapis zamiast jednego
    /// na pole — robi już dziennik zmian po drugiej stronie; tutaj jej brakowało.
    /// </para>
    /// </remarks>
    private Dictionary<(string Table, Guid Id, string Field), FieldStamp>? _known;

    private Dictionary<(string Table, Guid Id, string Field), FieldStamp> _tracked =>
        _known ??= db.ChangeTracker.Entries<FieldStamp>()
            .Where(e => e.State != EntityState.Deleted)
            .Select(e => e.Entity)
            .GroupBy(s => (s.EntityType, s.EntityId, s.Field))
            .ToDictionary(g => g.Key, g => g.First());

    /// <summary>Zapomnienie mapy — po zapisie, bo wtedy śledzenie wygląda inaczej.</summary>
    public void Forget() => _known = null;

    /// <summary>
    /// Znacznik pola — najpierw z tego, co już dopisane w tej transakcji, potem z bazy.
    /// </summary>
    /// <remarks>
    /// Kolejność ma znaczenie: w jednym przebiegu potrafią przyjść dwa wiersze tego
    /// samego pola, a świeżo dodany znacznik nie jest jeszcze zapisany, więc zapytanie
    /// do bazy by go nie zobaczyło i starszy wiersz nadpisałby nowszy.
    /// </remarks>
    private FieldStamp? Stamp(string table, Guid id, string pole)
    {
        if (_tracked.TryGetValue((table, id, pole), out var mine))
        {
            return mine;
        }

        // Z bazy po kluczu głównym, a nie zapytaniem po trzech polach: klucz tej tabeli
        // jest właśnie tą trójką, więc „Find" trafia w niego wprost i po drodze zagląda
        // do śledzenia bez przechodzenia go po kolei.
        var fromDb = db.FieldStamps.Find(table, id, pole);

        if (fromDb is not null)
        {
            _tracked[(table, id, pole)] = fromDb;
        }

        return fromDb;
    }

    /// <summary>
    /// Z postaci bazodanowej na typ właściwości. Droga odwrotna do tej, którą wartość
    /// przeszła przy zapisie do dziennika, więc przechodzi przez ten sam konwerter.
    /// </summary>
    private static object? Decode(JsonNode? value, IProperty property)
    {
        if (value is null)
        {
            return null;
        }

        var converter = property.GetValueConverter();
        var targetType = converter?.ProviderClrType ?? property.ClrType;
        var raw = value.Deserialize(Nullable.GetUnderlyingType(targetType) ?? targetType, Json);

        return converter is null ? raw : converter.ConvertFromProvider(raw);
    }
}
