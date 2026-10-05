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

    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        var sent = await PushAsync(ct);
        var applied = await PullAsync(ct);
        return new SyncReport(sent, applied);
    }

    /// <summary>Dopisuje niewysłane zmiany na koniec własnego pliku.</summary>
    public async Task<int> PushAsync(CancellationToken ct = default)
    {
        var pending = await _queue.RunAsync(
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

        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line.Serialize()).Append('\n');
        }

        await transport.WriteSegmentAsync(deviceId, await NextSegmentAsync(ct), text.ToString(), ct);

        await _queue.RunAsync(
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
        var cursors = await _queue.RunAsync(
            () => db.SyncCursors.ToDictionaryAsync(c => c.RemoteDeviceId, ct), ct);

        // Ściąganie **w całości przed** nakładaniem. Nakładanie przeplatane pobieraniem
        // trzymałoby bramę przez cały przebieg — czyli okno stałoby tak długo, jak długo
        // trwa sieć.
        var chunks = new List<(string Device, string Name, string Content)>();

        foreach (var group in segments.Where(s => s.DeviceId != deviceId).GroupBy(s => s.DeviceId))
        {
            var since = cursors.TryGetValue(group.Key, out var cursor)
                ? cursor.LastSegment
                : string.Empty;

            foreach (var segment in group.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (string.CompareOrdinal(segment.Name, since) <= 0)
                {
                    continue;
                }

                chunks.Add((group.Key, segment.Name, await transport.ReadSegmentAsync(segment, ct)));
            }
        }

        if (chunks.Count == 0)
        {
            return 0;
        }

        // ——— Nakładanie partiami ——————————————————————————————————————————————
        //
        // Brama brana na **kawałek porcji**, a nie na całe scalanie. Trzymanie jej przez
        // cały przebieg było w porządku, dopóki scalanie trwało chwilę — i przestało:
        // zapisane okno serii dopisuje do dziennika po kilkadziesiąt wierszy na każde
        // wystąpienie, więc porcja z drugiego urządzenia liczy dziś dziesiątki tysięcy
        // linii. Przez te minuty **nic innego nie docierało do bazy**: nie otwierała się
        // karta, nie zapisywało nowe zadanie, nie nastawiał się budzik. Z zewnątrz
        // wyglądało to jak aplikacja, która po minucie dobrej pracy nagle przestaje
        // reagować.
        //
        // Przerwanie w połowie porcji nic nie psuje: kursor przesuwa się dopiero po jej
        // ostatnim kawałku, a scalanie jest powtarzalne bez skutków ubocznych — linia
        // nałożona dwa razy przegrywa sama ze sobą po znaczniku zegara. Kosztem jest
        // powtórzenie kawałka, zyskiem aplikacja, która odpowiada.
        var applied = 0;

        foreach (var (device, name, content) in chunks)
        {
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            for (var from = 0; from < lines.Length; from += Block)
            {
                var slice = lines[from..Math.Min(from + Block, lines.Length)];
                var last = from + Block >= lines.Length;

                applied += await _queue.RunAsync(
                    () => ApplyAsync(device, name, slice, last, ct), ct);
            }

            // Porcja pusta też przesuwa kursor: bez tego pytalibyśmy o nią w kółko.
            if (lines.Length == 0)
            {
                await _queue.RunAsync(() => ApplyAsync(device, name, [], true, ct), ct);
            }
        }

        return applied;
    }

    /// <summary>Ile linii nakładamy na jedno wejście do bramy.</summary>
    /// <remarks>
    /// Tyle, żeby jedno wejście trwało ułamek sekundy nawet na telefonie. Liczba jest
    /// kompromisem: mniejsza znaczy więcej wejść i więcej zapisów, większa — dłuższą
    /// chwilę, w której reszta aplikacji czeka.
    /// </remarks>
    private const int Block = 500;

    /// <summary>
    /// Nałożenie kawałka porcji — za bramą, jedno wejście na kawałek.
    /// </summary>
    /// <remarks>
    /// Kursory czytane przy każdym wejściu od nowa i to jest konieczne, a nie
    /// marnotrawne: między kawałkami brama jest puszczona, więc stan sprzed poprzedniego
    /// nie jest już tym samym stanem.
    /// </remarks>
    private async Task<int> ApplyAsync(
        string device, string name, string[] lines, bool last, CancellationToken ct)
    {
        var applied = 0;

        // Kursory odczytane **raz**, na własność tego wejścia. Pytanie o kursor przy
        // każdej porcji z osobna wyglądało niewinnie, a było błędem: kursor dołożony
        // przy pierwszej porcji urządzenia nie jest jeszcze w bazie, więc drugie
        // pytanie nie widziało go i dokładało drugi.
        var cursors = await db.SyncCursors.ToDictionaryAsync(c => c.RemoteDeviceId, ct);

        using (SyncScope.Begin())
        {
            if (!cursors.TryGetValue(device, out var cursor))
            {
                cursor = new SyncCursor(device, string.Empty);
                db.SyncCursors.Add(cursor);
                cursors[device] = cursor;
            }

            foreach (var line in lines)
            {
                if (ChangeLine.TryParse(line) is { } row && _applier.Apply(row))
                {
                    applied++;
                }
            }

            // Kursor dopiero po ostatnim kawałku. Przesunięty wcześniej zgubiłby resztę
            // porcji, gdyby aplikacja zamknęła się w połowie.
            if (last)
            {
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
        var mine = (await transport.ListSegmentsAsync(ct))
            .Where(s => s.DeviceId == deviceId)
            .Select(s => int.TryParse(s.Name, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return (mine + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
