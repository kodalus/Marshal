using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Areas;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class AreaRepository(MarshalDbContext db, IDbQueue? queue = null)
    : IAreaRepository
{
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    public async Task<Area?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Areas.FirstOrDefaultAsync(a => a.Id == id && !a.Deleted, ct), ct);

    public async Task<IReadOnlyList<Area>> ActiveAsync(CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Areas
            .Where(a => a.IsActive && !a.Deleted)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Area>> AllAsync(CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Areas
            .Where(a => !a.Deleted)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct), ct);

    public void Add(Area area) => db.Areas.Add(area);
}
