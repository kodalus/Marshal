using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Series;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class TaskSeriesRepository(MarshalDbContext db, IDbQueue? queue = null)
    : ITaskSeriesRepository
{
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    public async Task<IReadOnlyList<TaskSeries>> ListAsync(CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.TaskSeries
            .Where(s => !s.Deleted)
            .ToListAsync(ct), ct);

    public async Task<TaskSeries?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _queue.RunAsync(
            () => db.TaskSeries.FirstOrDefaultAsync(s => s.Id == id && !s.Deleted, ct), ct);

    // Bez „!Deleted" i bez filtra po stanie — zob. ITaskSeriesRepository: nagrobek jest
    // tu informacją, a nie śmieciem. Dzień, który ktoś skasował, ma zostać skasowany.
    public async Task<IReadOnlyList<TaskItem>> OccurrencesAsync(
        Guid seriesId, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Tasks
            .Where(t => t.SeriesId == seriesId)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> AllOccurrencesAsync(
        CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Tasks
            .Where(t => t.SeriesId != null)
            .ToListAsync(ct), ct);

    // Bez „!Deleted": seria skasowana, której odbicia nie udało się jeszcze zdjąć,
    // dalej wskazuje na żyjące wydarzenie w Google — i dalej trzeba je odsiewać.
    public async Task<IReadOnlyList<string>> MirroredEventIdsAsync(
        CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.TaskSeries
            .Where(s => s.SharedEventId != null)
            .Select(s => s.SharedEventId!)
            .Distinct()
            .ToListAsync(ct), ct);

    public void Add(TaskSeries series) => db.TaskSeries.Add(series);
}
