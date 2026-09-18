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
/// <remarks>
/// Praca na bazie i praca w sieci są tu rozdzielone **celowo**. Kontekst bazy jest
/// pojedynczy na cały proces i nie znosi dwóch rzeczy naraz, a synchronizacja od
/// pewnego czasu rusza sama — więc jej dotknięcia bazy muszą przechodzić przez tę samą
/// bramę co zapisy z okna. Brama trzymana podczas pobierania z sieci blokowałaby okno
/// na cały przebieg; stąd kolejność „najpierw ściągnij wszystko, potem nałóż naraz".
/// </remarks>
public sealed class SyncEngine(
    MarshalDbContext db,
    ISyncTransport transport,
    IHlcSource hlc,
    string deviceId,
    IKolejkaBazy? kolejka = null)
{
    private readonly ChangeApplier _applier = new(db, hlc);

    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        var sent = await PushAsync(ct);
        var applied = await PullAsync(ct);
        return new SyncReport(sent, applied);
    }

    /// <summary>Dopisuje niewysłane zmiany na koniec własnego pliku.</summary>
    public async Task<int> PushAsync(CancellationToken ct = default)
    {
        var pending = await _kolejka.WykonajAsync(
            () => db.Changes.Where(c => !c.Sent).ToListAsync(ct), ct);

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

        await _kolejka.WykonajAsync(
            () =>
            {
                foreach (var change in pending)
                {
                    change.MarkSent();
                }

                return db.SaveChangesAsync(ct);
            },
            ct);

        return pending.Count;
    }

    /// <summary>Czyta pliki pozostałych urządzeń od zapisanego przesunięcia i scala.</summary>
    public async Task<int> PullAsync(CancellationToken ct = default)
    {
        var segments = await transport.ListSegmentsAsync(ct);

        // Kursory z bazy, przez bramę: dopiero one mówią, których porcji jeszcze nie
        // widzieliśmy, a bez tego trzeba by ściągać wszystko od początku świata.
        var kursory = await _kolejka.WykonajAsync(
            () => db.SyncCursors.ToDictionaryAsync(c => c.RemoteDeviceId, ct), ct);

        // Ściąganie **w całości przed** nakładaniem. Nakładanie przeplatane pobieraniem
        // trzymałoby bramę przez cały przebieg — czyli okno stałoby tak długo, jak długo
        // trwa sieć.
        var porcje = new List<(string Urzadzenie, string Nazwa, string Tresc)>();

        foreach (var grupa in segments.Where(s => s.DeviceId != deviceId).GroupBy(s => s.DeviceId))
        {
            var odkad = kursory.TryGetValue(grupa.Key, out var kursor)
                ? kursor.LastSegment
                : string.Empty;

            foreach (var segment in grupa.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (string.CompareOrdinal(segment.Name, odkad) <= 0)
                {
                    continue;
                }

                porcje.Add((grupa.Key, segment.Name, await transport.ReadSegmentAsync(segment, ct)));
            }
        }

        if (porcje.Count == 0)
        {
            return 0;
        }

        return await _kolejka.WykonajAsync(() => NalozAsync(porcje, ct), ct);
    }

    /// <summary>Nałożenie ściągniętych porcji — jednym blokiem, za bramą.</summary>
    private async Task<int> NalozAsync(
        List<(string Urzadzenie, string Nazwa, string Tresc)> porcje, CancellationToken ct)
    {
        var applied = 0;

        using (SyncScope.Begin())
        {
            foreach (var (urzadzenie, nazwa, tresc) in porcje)
            {
                var kursor = await db.SyncCursors
                    .FirstOrDefaultAsync(c => c.RemoteDeviceId == urzadzenie, ct);

                if (kursor is null)
                {
                    kursor = new SyncCursor(urzadzenie, string.Empty);
                    db.SyncCursors.Add(kursor);
                }

                foreach (var linia in tresc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ChangeLine.TryParse(linia) is { } wiersz && _applier.Apply(wiersz))
                    {
                        applied++;
                    }
                }

                kursor.MoveTo(nazwa);
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
