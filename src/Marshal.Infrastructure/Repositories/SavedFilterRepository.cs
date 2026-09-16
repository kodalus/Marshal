using Marshal.Application.Repositories;
using Marshal.Domain.Filters;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class SavedFilterRepository(MarshalDbContext db) : ISavedFilterRepository
{
    public async Task<SavedFilter?> FindAsync(Guid id, CancellationToken ct = default) =>
        await db.SavedFilters.FirstOrDefaultAsync(f => f.Id == id && !f.Deleted, ct);

    public async Task<IReadOnlyList<SavedFilter>> AllAsync(CancellationToken ct = default) =>
        await db.SavedFilters
            .Where(f => !f.Deleted)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.CreatedAt)
            .ToListAsync(ct);

    public void Add(SavedFilter filter) => db.SavedFilters.Add(filter);
}
