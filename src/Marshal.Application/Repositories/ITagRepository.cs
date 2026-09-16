using Marshal.Domain.Tags;

namespace Marshal.Application.Repositories;

public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> AllAsync(CancellationToken ct = default);

    Task<Tag?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>Szuka bez rozróżniania wielkości liter — „Dom" i „dom" to ten sam tag.</summary>
    Task<Tag?> FindByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Powiązania zadania, także zdjęte — zdjęte trzeba móc przywrócić zamiast tworzyć nowe.</summary>
    Task<IReadOnlyList<TaskTag>> LinksForTaskAsync(Guid taskId, CancellationToken ct = default);

    Task<IReadOnlyList<Tag>> ForTaskAsync(Guid taskId, CancellationToken ct = default);

    void Add(Tag tag);

    void AddLink(TaskTag link);
}
