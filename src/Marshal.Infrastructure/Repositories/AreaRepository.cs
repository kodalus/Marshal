using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Areas;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class AreaRepository(MarshalDbContext db, IKolejkaBazy? kolejka = null)
    : IAreaRepository
{
    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    public async Task<Area?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Areas.FirstOrDefaultAsync(a => a.Id == id && !a.Deleted, ct), ct);

    public async Task<IReadOnlyList<Area>> ActiveAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Areas
            .Where(a => a.IsActive && !a.Deleted)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Area>> AllAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Areas
            .Where(a => !a.Deleted)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct), ct);

    public void Add(Area area) => db.Areas.Add(area);
}
