using System.Text;
using System.Text.Json.Nodes;
using Marshal.Application.Abstractions;
using Marshal.Application.Sync;
using Marshal.Domain.Sync;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

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
    private readonly ChangeApplier _applier = new(db, hlc);

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

        await transport.WriteSegmentAsync(deviceId, await NextSegmentAsync(ct), tekst.ToString(), ct);

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
        var segments = await transport.ListSegmentsAsync(ct);
        var applied = 0;

        using (SyncScope.Begin())
        {
            foreach (var grupa in segments.Where(s => s.DeviceId != deviceId).GroupBy(s => s.DeviceId))
            {
                var cursor = await db.SyncCursors
                    .FirstOrDefaultAsync(c => c.RemoteDeviceId == grupa.Key, ct);

                if (cursor is null)
                {
                    cursor = new SyncCursor(grupa.Key, string.Empty);
                    db.SyncCursors.Add(cursor);
                }

                foreach (var segment in grupa.OrderBy(s => s.Name, StringComparer.Ordinal))
                {
                    if (string.CompareOrdinal(segment.Name, cursor.LastSegment) <= 0)
                    {
                        continue;
                    }

                    var tekst = await transport.ReadSegmentAsync(segment, ct);

                    foreach (var linia in tekst.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (ChangeLine.TryParse(linia) is { } wiersz && _applier.Apply(wiersz))
                        {
                            applied++;
                        }
                    }

                    cursor.MoveTo(segment.Name);
                }
            }

            // Scalanie podnosi zegar lokalny ponad znaczniki zdalne. Gdyby to
            // podniesienie nie przetrwało zamknięcia aplikacji, kolejna zmiana
            // lokalna byłaby wcześniejsza od tego, co już przyszło, i przegrałaby.
            LastHlcStore.Stage(db, hlc.Last);

            await db.SaveChangesAsync(ct);
        }

        return applied;
    }

    /// <summary>
    /// Kolejny numer porcji tego urządzenia. Uzupełniony zerami, żeby porządek
    /// leksykograficzny pokrywał się z chronologicznym — składnice sortują nazwy
    /// jako tekst, więc „10" wypadłoby przed „9".
    /// </summary>
    private async Task<string> NextSegmentAsync(CancellationToken ct)
    {
        var moje = (await transport.ListSegmentsAsync(ct))
            .Where(s => s.DeviceId == deviceId)
            .Select(s => int.TryParse(s.Name, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return (moje + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
