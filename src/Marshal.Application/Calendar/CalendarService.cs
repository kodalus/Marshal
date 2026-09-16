using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Calendar;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Calendar;

public sealed record CalendarRefreshReport(int Sources, int Events, int Failed);

/// <summary>
/// Kalendarz: odświeżanie źródeł i składanie siatki (spec 10.1, 11).
/// </summary>
public sealed class CalendarSyncService(
    ICalendarStore store,
    ITaskRepository tasks,
    IEnumerable<ICalendarFeed> feeds,
    IClock clock)
{
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

        foreach (var zrodlo in await store.SourcesAsync(ct))
        {
            var kursor = await store.CursorAsync(zrodlo.Id, ct);

            if (!force && kursor is not null && teraz - kursor.FetchedAt < RefreshInterval)
            {
                continue;
            }

            if (feeds.FirstOrDefault(f => f.Kind == zrodlo.Kind) is not { } kanal)
            {
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
            }
        }

        return new CalendarRefreshReport(odswiezone, wydarzen, nieudane);
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
