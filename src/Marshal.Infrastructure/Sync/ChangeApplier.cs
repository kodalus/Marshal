using System.Text.Json;
using System.Text.Json.Nodes;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Sync;
using Marshal.Infrastructure.Data;
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
    public bool Apply(ChangeLine wiersz)
    {
        var typ = db.Model.GetEntityTypes()
            .FirstOrDefault(t => t.GetTableName() == wiersz.Entity);

        // Nieznana tabela albo tabela lokalna: wpis z nowszej wersji aplikacji.
        // Pomijamy, zamiast przerywać — reszta pliku może być zrozumiała.
        if (typ is null || !typeof(Entity).IsAssignableFrom(typ.ClrType))
        {
            return false;
        }

        if (!Guid.TryParse(wiersz.Id, out var id) || !Hlc.TryParse(wiersz.Hlc, out var zdalny))
        {
            return false;
        }

        // Podniesienie zegara lokalnego ponad wszystko, co widzieliśmy, żeby kolejna
        // zmiana na tym urządzeniu była późniejsza od zdalnej (spec 3.5).
        hlc.Observe(zdalny);

        var encja = db.Find(typ.ClrType, id);

        if (encja is null)
        {
            encja = Activator.CreateInstance(typ.ClrType, nonPublic: true)
                ?? throw new InvalidOperationException($"Nie da się utworzyć {typ.ClrType}.");

            db.Add(encja);
            db.Entry(encja).Property("Id").CurrentValue = id;
        }

        var wpis = db.Entry(encja);
        var cokolwiek = false;

        foreach (var (pole, wartosc) in wiersz.Fields)
        {
            var wlasciwosc = typ.FindProperty(pole);

            if (wlasciwosc is null || wlasciwosc.IsPrimaryKey())
            {
                continue;
            }

            if (!Nowszy(wiersz.Entity, id, pole, zdalny))
            {
                continue;
            }

            wpis.Property(pole).CurrentValue = Decode(wartosc, wlasciwosc);
            Stamp(wiersz.Entity, id, pole, wiersz.Hlc);
            cokolwiek = true;
        }

        return cokolwiek;
    }

    /// <summary>
    /// Czy zdalna zmiana jest nowsza od tego, co mamy w tym **polu**.
    /// </summary>
    /// <remarks>
    /// Porównanie per pole, nie per encja. Gdyby porównywać znacznik encji,
    /// zmiana tytułu na jednym urządzeniu unieważniałaby zmianę wagi na drugim,
    /// mimo że dotyczą różnych rzeczy i obie są poprawne.
    /// </remarks>
    private bool Nowszy(string tabela, Guid id, string pole, Hlc zdalny)
    {
        var lokalny = Znacznik(tabela, id, pole);
        return lokalny is null || zdalny > Hlc.Parse(lokalny.Hlc);
    }

    private void Stamp(string tabela, Guid id, string pole, string znacznik)
    {
        if (Znacznik(tabela, id, pole) is { } istniejacy)
        {
            istniejacy.Update(znacznik);
        }
        else
        {
            db.Add(new FieldStamp(tabela, id, pole, znacznik));
        }
    }

    /// <summary>
    /// Znacznik pola — najpierw z tego, co już dopisane w tej transakcji, potem z bazy.
    /// </summary>
    /// <remarks>
    /// Kolejność ma znaczenie: w jednym przebiegu potrafią przyjść dwa wiersze tego
    /// samego pola, a świeżo dodany znacznik nie jest jeszcze zapisany, więc zapytanie
    /// do bazy by go nie zobaczyło i starszy wiersz nadpisałby nowszy.
    /// </remarks>
    private FieldStamp? Znacznik(string tabela, Guid id, string pole) =>
        db.ChangeTracker.Entries<FieldStamp>()
            .Select(e => e.Entity)
            .FirstOrDefault(s => s.EntityType == tabela && s.EntityId == id && s.Field == pole)
        ?? db.FieldStamps.FirstOrDefault(
            s => s.EntityType == tabela && s.EntityId == id && s.Field == pole);

    /// <summary>
    /// Z postaci bazodanowej na typ właściwości. Droga odwrotna do tej, którą wartość
    /// przeszła przy zapisie do dziennika, więc przechodzi przez ten sam konwerter.
    /// </summary>
    private static object? Decode(JsonNode? wartosc, IProperty wlasciwosc)
    {
        if (wartosc is null)
        {
            return null;
        }

        var konwerter = wlasciwosc.GetValueConverter();
        var typDocelowy = konwerter?.ProviderClrType ?? wlasciwosc.ClrType;
        var surowa = wartosc.Deserialize(Nullable.GetUnderlyingType(typDocelowy) ?? typDocelowy, Json);

        return konwerter is null ? surowa : konwerter.ConvertFromProvider(surowa);
    }
}
