using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Calendar;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Calendar;

public sealed class CalendarStore(MarshalDbContext db, IDbQueue? queue = null)
    : ICalendarStore
{
    // Ten sam wspólny kontekst, co wszędzie — a odświeżenie kalendarza chodzi po sieci
    // między odczytem a zapisem. To jest dokładnie ta szczelina, w którą potrafi wejść
    // synchronizacja ruszająca sama, więc przez bramę idą i odczyty, i zapisy.
    private readonly IDbQueue _kolejka = queue ?? new KolejkaWprost();

    public async Task<IReadOnlyList<CalendarSource>> SourcesAsync(CancellationToken ct = default) =>
        await _kolejka.RunAsync(() => db.CalendarSources
            .Where(s => !s.Deleted)
            .OrderBy(s => s.Name)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<CalendarSource>> AllSourcesAsync(CancellationToken ct = default) =>
        await _kolejka.RunAsync(() => db.CalendarSources
            .OrderBy(s => s.Name)
            .ToListAsync(ct), ct);

    public Task<CalendarCursor?> CursorAsync(Guid sourceId, CancellationToken ct = default) =>
        _kolejka.RunAsync(
            () => db.CalendarCursors.FirstOrDefaultAsync(c => c.SourceId == sourceId, ct), ct);

    public void SaveCursor(Guid sourceId, string? syncToken, DateTimeOffset fetchedAt)
    {
        var istniejacy = db.ChangeTracker.Entries<CalendarCursor>()
                .Select(e => e.Entity)
                .FirstOrDefault(c => c.SourceId == sourceId)
            ?? db.CalendarCursors.FirstOrDefault(c => c.SourceId == sourceId);

        if (istniejacy is null)
        {
            db.CalendarCursors.Add(new CalendarCursor(sourceId, syncToken, fetchedAt));
        }
        else
        {
            istniejacy.Update(syncToken, fetchedAt);
        }
    }

    public async Task<IReadOnlyList<CalendarEvent>> EventsAsync(
        DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default)
    {
        return await _kolejka.RunAsync<IReadOnlyList<CalendarEvent>>(async () =>
        {
            var widoczne = await db.CalendarSources
                .Where(s => s.IsVisible && !s.Deleted)
                .Select(s => s.Id)
                .ToListAsync(ct);

            // Warunek zachodzenia zakresów, nie „start w zakresie": wydarzenie zaczęte wczoraj
            // i trwające do jutra musi się pokazać na dzisiejszej siatce.
            return await db.CalendarEvents
                .Where(e => widoczne.Contains(e.SourceId) && !e.Cancelled)
                .Where(e => e.StartsAt < until && e.EndsAt > from)
                .OrderBy(e => e.StartsAt)
                .ToListAsync(ct);
        }, ct);
    }

    public async Task UpsertAsync(
        Guid sourceId, IReadOnlyList<FeedEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        // Jedno zapytanie na całą porcję, nie jedno na wydarzenie. Kalendarz po pełnym
        // odczycie potrafi mieć kilkaset pozycji, a każda osobno to kilkaset zapytań.
        var klucze = events.Select(e => e.ExternalId).ToList();
        var existing = await _kolejka.RunAsync(() => db.CalendarEvents
            .Where(e => e.SourceId == sourceId && klucze.Contains(e.ExternalId))
            .ToDictionaryAsync(e => e.ExternalId, ct), ct);

        foreach (var ev in events)
        {
            if (existing.TryGetValue(ev.ExternalId, out var zapisane))
            {
                zapisane.Update(
                    ev.Title, ev.StartsAt, ev.EndsAt,
                    ev.IsAllDay, ev.Location, ev.Cancelled);
            }
            else
            {
                db.CalendarEvents.Add(new CalendarEvent(
                    sourceId, ev.ExternalId, ev.Title,
                    ev.StartsAt, ev.EndsAt, ev.IsAllDay,
                    ev.Location, ev.Cancelled));
            }
        }
    }

    public async Task<int> MarkMissingCancelledAsync(
        Guid sourceId, IReadOnlyList<string> seen, CancellationToken ct = default)
    {
        var widziane = seen.ToHashSet(StringComparer.Ordinal);

        var zniknione = await _kolejka.RunAsync(() => db.CalendarEvents
            .Where(e => e.SourceId == sourceId && !e.Cancelled)
            .ToListAsync(ct), ct);

        var policzone = 0;

        foreach (var ev in zniknione.Where(e => !widziane.Contains(e.ExternalId)))
        {
            // Nagrobek, nie usunięcie — tak samo jak wszędzie indziej w tym modelu.
            ev.Update(
                ev.Title, ev.StartsAt, ev.EndsAt,
                ev.IsAllDay, ev.Location, cancelled: true);
            policzone++;
        }

        return policzone;
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        _kolejka.RunAsync(() => db.CalendarEvents.CountAsync(e => !e.Cancelled, ct), ct);

    public void AddSource(CalendarSource source) => db.CalendarSources.Add(source);

    public async Task<int> ForgetEventsAsync(Guid sourceId, CancellationToken ct = default) =>
        await _kolejka.RunAsync(
            async () =>
            {
                var count = await db.CalendarEvents
                    .Where(e => e.SourceId == sourceId)
                    .ExecuteDeleteAsync(ct);

                await db.CalendarCursors
                    .Where(c => c.SourceId == sourceId)
                    .ExecuteDeleteAsync(ct);

                return count;
            },
            ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        _kolejka.RunAsync(() => db.SaveChangesAsync(ct), ct);
}
