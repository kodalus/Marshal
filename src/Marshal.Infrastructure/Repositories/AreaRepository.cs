using Marshal.Application.Repositories;
using Marshal.Domain.Areas;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class AreaRepository(MarshalDbContext db) : IAreaRepository
{
    public async Task<Area?> FindAsync(Guid id, CancellationToken ct = default) =>
        await db.Areas.FirstOrDefaultAsync(a => a.Id == id && !a.Deleted, ct);

    public async Task<IReadOnlyList<Area>> ActiveAsync(CancellationToken ct = default) =>
        await db.Areas
            .Where(a => a.IsActive && !a.Deleted)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct);

    public void Add(Area area) => db.Areas.Add(area);
}
