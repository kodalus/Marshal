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

    public void Add(TaskItem task) => db.Tasks.Add(task);
}
