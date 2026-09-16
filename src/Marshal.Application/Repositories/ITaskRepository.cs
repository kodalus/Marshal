using Marshal.Domain.Tasks;

namespace Marshal.Application.Repositories;

public interface ITaskRepository
{
    Task<TaskItem?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>Skrzynka, najstarsze pierwsze — przetwarza się w kolejności wrzucania.</summary>
    Task<IReadOnlyList<TaskItem>> InboxAsync(CancellationToken ct = default);

    Task<int> InboxCountAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> ByStateAsync(TaskState state, CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> ByProjectAsync(Guid projectId, CancellationToken ct = default);

    void Add(TaskItem task);
}
