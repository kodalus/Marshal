using Marshal.Domain.Areas;

namespace Marshal.Application.Repositories;

public interface IAreaRepository
{
    Task<Area?> FindAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Area>> ActiveAsync(CancellationToken ct = default);

    void Add(Area area);
}
