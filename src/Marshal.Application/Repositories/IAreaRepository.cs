using Marshal.Domain.Areas;

namespace Marshal.Application.Repositories;

public interface IAreaRepository
{
    Task<Area?> FindAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Area>> ActiveAsync(CancellationToken ct = default);

    /// <summary>Wszystkie, także nieaktywne — ekran struktury musi je pokazać, żeby dało się je włączyć.</summary>
    Task<IReadOnlyList<Area>> AllAsync(CancellationToken ct = default);

    void Add(Area area);
}
