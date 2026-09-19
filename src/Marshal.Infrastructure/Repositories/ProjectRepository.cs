using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Projects;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class ProjectRepository(MarshalDbContext db, IDbQueue? queue = null)
    : IProjectRepository
{
    private readonly IDbQueue _kolejka = queue ?? new KolejkaWprost();

    public async Task<Project?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _kolejka.RunAsync(() => db.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.Deleted, ct), ct);

    public async Task<IReadOnlyList<Project>> ActiveAsync(CancellationToken ct = default) =>
        await _kolejka.RunAsync(() => db.Projects
            .Where(p => p.State == ProjectState.Active && !p.Deleted)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Project>> AllAsync(CancellationToken ct = default) =>
        await _kolejka.RunAsync(() => db.Projects
            .Where(p => !p.Deleted)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Project>> ByAreaAsync(Guid areaId, CancellationToken ct = default) =>
        await _kolejka.RunAsync(() => db.Projects
            .Where(p => p.AreaId == areaId && !p.Deleted)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct), ct);

    public void Add(Project project) => db.Projects.Add(project);
}
