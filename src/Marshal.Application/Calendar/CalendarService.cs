using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Calendar;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Calendar;

/// <summary>
/// Wynik odświeżania. **Z powodami**, nie samą liczbą porażek.
/// </summary>
/// <remarks>
/// Sama liczba nie mówi nic: „nieudanych 6" wygląda tak samo, gdy nie ma zgody na
/// kalendarz, gdy adres kanału jest zły i gdy padła sieć. Kalendarz zewnętrzny jest
/// jedynym miejscem w aplikacji, gdzie **wszystkie** przyczyny są po cudzej stronie,
/// więc powód jest tu jedyną rzeczą, z której da się cokolwiek zrobić.
/// </remarks>
public sealed record CalendarRefreshReport(
    int Sources, int Events, int Failed, IReadOnlyList<string> Problems)
{
    public CalendarRefreshReport(int sources, int events, int failed)
        : this(sources, events, failed, [])
    {
    }
}

/// <summary>
/// Kalendarz: odświeżanie źródeł i składanie siatki (spec 10.1, 11).
/// </summary>
public sealed class CalendarSyncService(
    ICalendarStore store,
    ITaskRepository tasks,
    IEnumerable<ICalendarFeed> feeds,
    IClock clock,
    IHlcSource hlc)
{
    public Task<IReadOnlyList<CalendarSource>> SourcesAsync(CancellationToken ct = default) =>
        store.SourcesAsync(ct);

    /// <summary>
    /// Podłączenie kalendarza. Do etapu 10 nie było **żadnej** drogi, żeby to zrobić:
    /// odświeżanie przechodziło po źródłach, których nikt nie umiał dodać.
    /// </summary>
    public async Task<CalendarSource> AddAsync(
        CalendarKind kind, string externalId, string name, CancellationToken ct = default)
    {
        // Ten sam kalendarz dwa razy to zawsze pomyłka — najczęściej klikanie „Dodaj"
        // w reakcji na to, że nic się nie pojawiło. Duplikaty mnożą potem te same
        // błędy w raporcie i zaciemniają jedyny, który coś znaczy.
        var szukany = externalId?.Trim() ?? string.Empty;

        if ((await store.SourcesAsync(ct)).FirstOrDefault(
                z => z.Kind == kind && z.ExternalId == szukany) is { } juzJest)
        {
            return juzJest;
        }

        var zrodlo = new CalendarSource(
            Guid.CreateVersion7(), clock.Now, hlc.Next(), kind, externalId, name);

        store.AddSource(zrodlo);
        await store.SaveChangesAsync(ct);

        return zrodlo;
    }

    /// <summary>Odłączenie. Nagrobek, nie usunięcie — wybór kalendarzy się synchronizuje.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        if ((await store.SourcesAsync(ct)).FirstOrDefault(z => z.Id == id) is not { } zrodlo)
        {
            return;
        }

        zrodlo.MarkDeleted(hlc.Next());
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Co ile odświeżać kanały (spec 10.1).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Odświeża źródła, którym minął czas. Zwraca też liczbę tych, które nie odpowiedziały.
    /// </summary>
    /// <remarks>
    /// Awaria jednego kanału nie może zatrzymać pozostałych ani wywrócić startu aplikacji.
    /// Kalendarz jest dodatkiem do zadań; niedostępny kanał ma znaczyć „brak świeżych
    /// wydarzeń", a nie „aplikacja się nie otwiera".
    /// </remarks>
    public async Task<CalendarRefreshReport> RefreshAsync(
        bool force = false, CancellationToken ct = default)
    {
        var teraz = clock.Now;
        var odswiezone = 0;
        var wydarzen = 0;
        var nieudane = 0;
        var powody = new List<string>();

        foreach (var zrodlo in await store.SourcesAsync(ct))
        {
            var kursor = await store.CursorAsync(zrodlo.Id, ct);

            if (!force && kursor is not null && teraz - kursor.FetchedAt < RefreshInterval)
            {
                continue;
            }

            if (feeds.FirstOrDefault(f => f.Kind == zrodlo.Kind) is not { } kanal)
            {
                // Rodzaj bez podłączonego kanału. Do dziś było to ciche pominięcie
                // i właśnie ono sprawiało, że kalendarz Google wyglądał na pusty.
                nieudane++;
                powody.Add($"{zrodlo.Name}: brak obsługi kalendarzy rodzaju {zrodlo.Kind}.");
                continue;
            }

            try
            {
                var wynik = await kanal.FetchAsync(zrodlo, kursor?.SyncToken, ct);

                await store.UpsertAsync(zrodlo.Id, wynik.Events, ct);

                if (wynik.IsFull)
                {
                    await store.MarkMissingCancelledAsync(
                        zrodlo.Id, wynik.Events.Select(e => e.ExternalId).ToList(), ct);
                }

                store.SaveCursor(zrodlo.Id, wynik.SyncToken, teraz);
                await store.SaveChangesAsync(ct);

                odswiezone++;
                wydarzen += wynik.Events.Count;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                nieudane++;
                powody.Add($"{zrodlo.Name}: {e.Message}");
            }
        }

        return new CalendarRefreshReport(odswiezone, wydarzen, nieudane, powody);
    }

    /// <summary>
    /// Siatka na zadany zakres dni: wydarzenia z kalendarzy plus zadania.
    /// </summary>
    /// <remarks>
    /// Zadania lądują na tej samej siatce, ale półprzezroczyste (spec 11): wydarzenie
    /// jest umową z kimś, zadanie zamiarem wobec siebie, i te dwie rzeczy nie mogą
    /// wyglądać tak samo.
    /// </remarks>
    public async Task<IReadOnlyList<AgendaDay>> AgendaAsync(
        DateOnly from, int days, CancellationToken ct = default)
    {
        var strefa = clock.Now.Offset;
        var poczatek = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), strefa);
        var koniec = poczatek.AddDays(days);

        var wpisy = new List<AgendaEntry>();

        foreach (var wydarzenie in await store.EventsAsync(poczatek, koniec, ct))
        {
            wpisy.Add(new AgendaEntry(
                wydarzenie.Title,
                wydarzenie.StartsAt.ToOffset(strefa),
                wydarzenie.EndsAt.ToOffset(strefa),
                wydarzenie.IsAllDay,
                AgendaKind.Event,
                Color: null,
                TaskId: null));
        }

        foreach (var zadanie in await tasks.UpcomingAsync(from.AddDays(-1), from.AddDays(days), ct))
        {
            if (Entry(zadanie, strefa) is { } wpis)
            {
                wpisy.Add(wpis);
            }
        }

        return Agenda.Build(wpisy, from, days);
    }

    /// <summary>
    /// Zadanie na siatce. Bez godziny ląduje na pasku całodniowym — zgadywanie godziny
    /// zrobiłoby z listy zadań kalendarz, w którym wszystko jest umówione, a to jest
    /// dokładnie ten rodzaj planowania, który się nie utrzymuje.
    /// </summary>
    private static AgendaEntry? Entry(TaskItem task, TimeSpan zone)
    {
        if (task.DoDate is not { } dzien)
        {
            return null;
        }

        if (task.DoTime is not { } godzina)
        {
            var poczatekDnia = new DateTimeOffset(dzien.ToDateTime(TimeOnly.MinValue), zone);
            return new AgendaEntry(
                task.Title, poczatekDnia, poczatekDnia.AddDays(1),
                IsAllDay: true, AgendaKind.Task, task.Color, task.Id);
        }

        var start = new DateTimeOffset(dzien.ToDateTime(godzina), zone);
        var dlugosc = TimeSpan.FromMinutes(task.EstimatedMinutes ?? 30);

        return new AgendaEntry(
            task.Title, start, start + dlugosc, IsAllDay: false, AgendaKind.Task, task.Color, task.Id);
    }
}
