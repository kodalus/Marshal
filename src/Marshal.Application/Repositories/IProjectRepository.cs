using Marshal.Domain.Projects;

namespace Marshal.Application.Repositories;

public interface IProjectRepository
{
    Task<Project?> FindAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Project>> ActiveAsync(CancellationToken ct = default);

    void Add(Project project);
}
