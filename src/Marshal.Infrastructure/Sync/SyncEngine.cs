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
    IDbQueue? queue = null)
{
    private readonly ChangeApplier _applier = new(db, hlc);

    private readonly IDbQueue _kolejka = queue ?? new KolejkaWprost();

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        var sent = await PushAsync(ct);
        var applied = await PullAsync(ct);
        return new SyncReport(sent, applied);
    }

    /// <summary>Dopisuje niewysłane zmiany na koniec własnego pliku.</summary>
    public async Task<int> PushAsync(CancellationToken ct = default)
    {
        var pending = await _kolejka.RunAsync(
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

        await _kolejka.RunAsync(
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
        var kursory = await _kolejka.RunAsync(
            () => db.SyncCursors.ToDictionaryAsync(c => c.RemoteDeviceId, ct), ct);

        // Ściąganie **w całości przed** nakładaniem. Nakładanie przeplatane pobieraniem
        // trzymałoby bramę przez cały przebieg — czyli okno stałoby tak długo, jak długo
        // trwa sieć.
        var porcje = new List<(string Urzadzenie, string Nazwa, string Tresc)>();

        foreach (var group in segments.Where(s => s.DeviceId != deviceId).GroupBy(s => s.DeviceId))
        {
            var odkad = kursory.TryGetValue(group.Key, out var cursor)
                ? cursor.LastSegment
                : string.Empty;

            foreach (var segment in group.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (string.CompareOrdinal(segment.Name, odkad) <= 0)
                {
                    continue;
                }

                porcje.Add((group.Key, segment.Name, await transport.ReadSegmentAsync(segment, ct)));
            }
        }

        if (porcje.Count == 0)
        {
            return 0;
        }

        return await _kolejka.RunAsync(() => NalozAsync(porcje, ct), ct);
    }

    /// <summary>Nałożenie ściągniętych porcji — jednym blokiem, za bramą.</summary>
    private async Task<int> NalozAsync(
        List<(string Urzadzenie, string Nazwa, string Tresc)> porcje, CancellationToken ct)
    {
        var applied = 0;

        // Kursory odczytane **raz**, na własność tego bloku. Pytanie o kursor przy każdej
        // porcji z osobna wyglądało niewinnie, a było błędem: kursor dołożony przy
        // pierwszej porcji urządzenia nie jest jeszcze w bazie, więc drugie pytanie nie
        // widziało go i dokładało drugi. Zapytanie nie widzi tego, co czeka na zapis.
        //
        // Czytane tutaj, a nie przekazane z góry: brama była puszczona na czas sieci,
        // więc stan sprzed pobierania nie jest już tym samym stanem.
        var kursory = await db.SyncCursors.ToDictionaryAsync(c => c.RemoteDeviceId, ct);

        using (SyncScope.Begin())
        {
            foreach (var (urzadzenie, name, content) in porcje)
            {
                if (!kursory.TryGetValue(urzadzenie, out var cursor))
                {
                    cursor = new SyncCursor(urzadzenie, string.Empty);
                    db.SyncCursors.Add(cursor);
                    kursory[urzadzenie] = cursor;
                }

                foreach (var linia in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ChangeLine.TryParse(linia) is { } wiersz && _applier.Apply(wiersz))
                    {
                        applied++;
                    }
                }

                cursor.MoveTo(name);
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
