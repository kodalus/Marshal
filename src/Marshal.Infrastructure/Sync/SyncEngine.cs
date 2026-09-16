using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marshal.Application.Abstractions;
using Marshal.Application.Sync;
using Marshal.Domain.Primitives;
using Marshal.Domain.Sync;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Marshal.Infrastructure.Sync;

public sealed record SyncReport(int Sent, int Applied);

/// <summary>
/// Wysyłka własnych zmian i scalanie cudzych (spec 9.4).
/// </summary>
public sealed class SyncEngine(
    MarshalDbContext db,
    ISyncTransport transport,
    IHlcSource hlc,
    string deviceId)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        var sent = await PushAsync(ct);
        var applied = await PullAsync(ct);
        return new SyncReport(sent, applied);
    }

    /// <summary>Dopisuje niewysłane zmiany na koniec własnego pliku.</summary>
    public async Task<int> PushAsync(CancellationToken ct = default)
    {
        var pending = await db.Changes.Where(c => !c.Sent).ToListAsync(ct);

        if (pending.Count == 0)
        {
            return 0;
        }

        var lines = pending
            .GroupBy(c => (c.EntityType, c.EntityId, c.Hlc))
            .OrderBy(g => g.Key.Hlc, StringComparer.Ordinal)
            .Select(g => new ChangeLine
            {
                Entity = g.Key.EntityType,
                Id = g.Key.EntityId.ToString(),
                Hlc = g.Key.Hlc,
                Fields = g.ToDictionary(
                    c => c.Field,
                    c => c.Value is null ? null : JsonNode.Parse(c.Value)),
            });

        var tekst = new StringBuilder();
        foreach (var line in lines)
        {
            tekst.Append(line.Serialize()).Append('\n');
        }

        await transport.AppendAsync(deviceId, tekst.ToString(), ct);

        foreach (var change in pending)
        {
            change.MarkSent();
        }

        await db.SaveChangesAsync(ct);
        return pending.Count;
    }

    /// <summary>Czyta pliki pozostałych urządzeń od zapisanego przesunięcia i scala.</summary>
    public async Task<int> PullAsync(CancellationToken ct = default)
    {
        var logs = await transport.ListLogsAsync(ct);
        var applied = 0;

        using (SyncScope.Begin())
        {
            foreach (var log in logs.Where(l => l.DeviceId != deviceId))
            {
                var cursor = await db.SyncCursors
                    .FirstOrDefaultAsync(c => c.RemoteDeviceId == log.DeviceId, ct);

                if (cursor is null)
                {
                    cursor = new SyncCursor(log.DeviceId, 0);
                    db.SyncCursors.Add(cursor);
                }

                var tekst = await transport.ReadFromAsync(log.DeviceId, cursor.Offset, ct);

                if (tekst.Length == 0)
                {
                    continue;
                }

                // Wyłącznie pełne wiersze. Ostatnia linia może być urwana, bo drugie
                // urządzenie mogło zapisywać w trakcie naszego czytania. Urwany ogon
                // zostaje na następny raz — wtedy plik będzie już kompletny.
                var koniec = tekst.LastIndexOf('\n');

                if (koniec < 0)
                {
                    continue;
                }

                var pelne = tekst[..(koniec + 1)];

                foreach (var linia in pelne.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ChangeLine.TryParse(linia) is { } wiersz && Apply(wiersz))
                    {
                        applied++;
                    }
                }

                cursor.MoveTo(cursor.Offset + Encoding.UTF8.GetByteCount(pelne));
            }

            await db.SaveChangesAsync(ct);
        }

        return applied;
    }

    private bool Apply(ChangeLine wiersz)
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
        var lokalny = db.ChangeTracker.Entries<FieldStamp>()
                .Select(e => e.Entity)
                .FirstOrDefault(s => s.EntityType == tabela && s.EntityId == id && s.Field == pole)
            ?? db.FieldStamps.FirstOrDefault(s => s.EntityType == tabela && s.EntityId == id && s.Field == pole);

        return lokalny is null || zdalny > Hlc.Parse(lokalny.Hlc);
    }

    private void Stamp(string tabela, Guid id, string pole, string znacznik)
    {
        var istniejacy = db.ChangeTracker.Entries<FieldStamp>()
                .Select(e => e.Entity)
                .FirstOrDefault(s => s.EntityType == tabela && s.EntityId == id && s.Field == pole)
            ?? db.FieldStamps.FirstOrDefault(s => s.EntityType == tabela && s.EntityId == id && s.Field == pole);

        if (istniejacy is null)
        {
            db.Add(new FieldStamp(tabela, id, pole, znacznik));
        }
        else
        {
            istniejacy.Update(znacznik);
        }
    }

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
