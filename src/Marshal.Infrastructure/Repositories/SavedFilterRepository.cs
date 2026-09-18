using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Filters;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class SavedFilterRepository(MarshalDbContext db, IKolejkaBazy? kolejka = null)
    : ISavedFilterRepository
{
    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    public async Task<SavedFilter?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.SavedFilters.FirstOrDefaultAsync(f => f.Id == id && !f.Deleted, ct), ct);

    public async Task<IReadOnlyList<SavedFilter>> AllAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.SavedFilters
            .Where(f => !f.Deleted)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.CreatedAt)
            .ToListAsync(ct), ct);

    public void Add(SavedFilter filter) => db.SavedFilters.Add(filter);
}
