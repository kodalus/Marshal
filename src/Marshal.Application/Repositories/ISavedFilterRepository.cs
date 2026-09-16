using Marshal.Domain.Filters;

namespace Marshal.Application.Repositories;

public interface ISavedFilterRepository
{
    Task<SavedFilter?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>Ulubione w kolejności ustawionej ręcznie.</summary>
    Task<IReadOnlyList<SavedFilter>> AllAsync(CancellationToken ct = default);

    void Add(SavedFilter filter);
}
