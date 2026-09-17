using Marshal.Application.Calendar;
using Marshal.Domain.Calendar;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Calendar;

public sealed class CalendarStore(MarshalDbContext db) : ICalendarStore
{
    public async Task<IReadOnlyList<CalendarSource>> SourcesAsync(CancellationToken ct = default) =>
        await db.CalendarSources
            .Where(s => !s.Deleted)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

    public Task<CalendarCursor?> CursorAsync(Guid sourceId, CancellationToken ct = default) =>
        db.CalendarCursors.FirstOrDefaultAsync(c => c.SourceId == sourceId, ct);

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
        var istniejace = await db.CalendarEvents
            .Where(e => e.SourceId == sourceId && klucze.Contains(e.ExternalId))
            .ToDictionaryAsync(e => e.ExternalId, ct);

        foreach (var wydarzenie in events)
        {
            if (istniejace.TryGetValue(wydarzenie.ExternalId, out var zapisane))
            {
                zapisane.Update(
                    wydarzenie.Title, wydarzenie.StartsAt, wydarzenie.EndsAt,
                    wydarzenie.IsAllDay, wydarzenie.Location, wydarzenie.Cancelled);
            }
            else
            {
                db.CalendarEvents.Add(new CalendarEvent(
                    sourceId, wydarzenie.ExternalId, wydarzenie.Title,
                    wydarzenie.StartsAt, wydarzenie.EndsAt, wydarzenie.IsAllDay,
                    wydarzenie.Location, wydarzenie.Cancelled));
            }
        }
    }

    public async Task<int> MarkMissingCancelledAsync(
        Guid sourceId, IReadOnlyList<string> seen, CancellationToken ct = default)
    {
        var widziane = seen.ToHashSet(StringComparer.Ordinal);

        var zniknione = await db.CalendarEvents
            .Where(e => e.SourceId == sourceId && !e.Cancelled)
            .ToListAsync(ct);

        var policzone = 0;

        foreach (var wydarzenie in zniknione.Where(e => !widziane.Contains(e.ExternalId)))
        {
            // Nagrobek, nie usunięcie — tak samo jak wszędzie indziej w tym modelu.
            wydarzenie.Update(
                wydarzenie.Title, wydarzenie.StartsAt, wydarzenie.EndsAt,
                wydarzenie.IsAllDay, wydarzenie.Location, cancelled: true);
            policzone++;
        }

        return policzone;
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        db.CalendarEvents.CountAsync(e => !e.Cancelled, ct);

    public void AddSource(CalendarSource source) => db.CalendarSources.Add(source);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
