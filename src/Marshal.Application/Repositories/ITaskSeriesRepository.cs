using Marshal.Domain.Series;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Repositories;

public interface ITaskSeriesRepository
{
    /// <summary>Serie żywe — te, które mają jeszcze co produkować.</summary>
    Task<IReadOnlyList<TaskSeries>> ListAsync(CancellationToken ct = default);

    Task<TaskSeries?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Wszystkie wystąpienia serii — <b>razem z nagrobkami i odhaczonymi</b>.
    /// </summary>
    /// <remarks>
    /// Nagrobki są tu najważniejsze. Dopełnianie okna pomija dni, które już mają wiersz,
    /// a wiersz skasowany ma zostać skasowany: bez nagrobków dopełnienie wskrzeszałoby
    /// każde „tej środy nie będzie" przy najbliższym uruchomieniu.
    /// </remarks>
    Task<IReadOnlyList<TaskItem>> OccurrencesAsync(Guid seriesId, CancellationToken ct = default);

    /// <summary>Wystąpienia wszystkich serii naraz — do dopełniania okna jednym przebiegiem.</summary>
    Task<IReadOnlyList<TaskItem>> AllOccurrencesAsync(CancellationToken ct = default);

    void Add(TaskSeries series);
}
