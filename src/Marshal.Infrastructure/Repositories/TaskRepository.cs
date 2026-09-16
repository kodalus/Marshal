using Marshal.Application.Repositories;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class TaskRepository(MarshalDbContext db) : ITaskRepository
{
    public async Task<TaskItem?> FindAsync(Guid id, CancellationToken ct = default) =>
        await db.Tasks.FirstOrDefaultAsync(t => t.Id == id && !t.Deleted, ct);

    public async Task<IReadOnlyList<TaskItem>> InboxAsync(CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => t.State == TaskState.Inbox && !t.Deleted)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public Task<int> InboxCountAsync(CancellationToken ct = default) =>
        db.Tasks.CountAsync(t => t.State == TaskState.Inbox && !t.Deleted, ct);

    public async Task<IReadOnlyList<TaskItem>> ByStateAsync(TaskState state, CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => t.State == state && !t.Deleted)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> ByProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => t.ProjectId == projectId && !t.Deleted && t.State != TaskState.Trashed)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> TodayAsync(DateOnly today, CancellationToken ct = default) =>
        await Otwarte()
            .Where(t => (t.DoDate != null && t.DoDate <= today)
                     || (t.Deadline != null && t.Deadline <= today))
            .OrderBy(t => t.Deadline == null)
            .ThenBy(t => t.Deadline)
            .ThenBy(t => t.DoDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> UpcomingAsync(
        DateOnly after, DateOnly until, CancellationToken ct = default) =>
        await Otwarte()
            .Where(t => (t.DoDate != null && t.DoDate > after && t.DoDate <= until)
                     || (t.Deadline != null && t.Deadline > after && t.Deadline <= until))
            .OrderBy(t => t.DoDate ?? t.Deadline)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> ArchiveAsync(int limit, CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => !t.Deleted && (t.State == TaskState.Done || t.State == TaskState.Trashed))
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> ByAreaAsync(Guid areaId, CancellationToken ct = default) =>
        await Otwarte()
            .Where(t => t.AreaId == areaId)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> OverdueByDoDateAsync(
        DateOnly today, CancellationToken ct = default) =>
        await Otwarte()
            .Where(t => t.DoDate != null && t.DoDate < today)
            // Najstarsze pierwsze: przy Accumulate kolejność ma znaczenie, bo każde
            // wystąpienie rodzi następne i rytm musi wyjść z najdawniejszego.
            .OrderBy(t => t.DoDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> DueRemindersAsync(
        DateTimeOffset now, CancellationToken ct = default) =>
        await Otwarte()
            .Where(t => t.ReminderAt != null && t.ReminderAt <= now)
            // Najdawniejsze pierwsze: gdy uzbierało się kilka, kolejność ma być taka,
            // w jakiej miały się odezwać.
            .OrderBy(t => t.ReminderAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> ByFocusDateAsync(
        DateOnly date, CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => t.FocusDate == date && !t.Deleted && t.State != TaskState.Trashed)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> ExpiredFocusAsync(
        DateOnly today, CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => t.FocusDate != null && t.FocusDate < today && !t.Deleted
                     && t.State != TaskState.Done && t.State != TaskState.Trashed)
            .ToListAsync(ct);

    /// <summary>Zadania nierozstrzygnięte: poza skrzynką, koszem i wykonanymi.</summary>
    private IQueryable<TaskItem> Otwarte() =>
        db.Tasks.Where(t => !t.Deleted
                         && t.State != TaskState.Done
                         && t.State != TaskState.Trashed
                         && t.State != TaskState.Inbox);

    public void Add(TaskItem task) => db.Tasks.Add(task);
}
