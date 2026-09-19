using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Sync;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class ReminderLog(MarshalDbContext db, IDbQueue? queue = null)
    : IReminderLog
{
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    /// <summary>
    /// Czy ta konkretna chwila już się odezwała.
    /// </summary>
    /// <remarks>
    /// Para zadanie-chwila, nie samo zadanie. Zadanie z trzema wyprzedzeniami ma trzy
    /// chwile i każda odzywa się raz; przesunięcie zadania daje nowe chwile, więc
    /// odzywa się na nowo — i to jest właściwe, bo to jest inna pora niż poprzednio.
    /// </remarks>
    public Task<bool> WasShownAsync(
        Guid taskId, DateTimeOffset reminderAt, CancellationToken ct = default) =>
        _queue.RunAsync(async () =>
            db.ChangeTracker.Entries<ReminderShown>()
                .Select(e => e.Entity)
                .Any(r => r.TaskId == taskId && r.ReminderAt == reminderAt)
            || await db.ReminderShown.AsNoTracking()
                .AnyAsync(r => r.TaskId == taskId && r.ReminderAt == reminderAt, ct), ct);

    public void Record(Guid taskId, DateTimeOffset reminderAt, DateTimeOffset shownAt)
    {
        var alreadyThere = db.ChangeTracker.Entries<ReminderShown>()
            .Select(e => e.Entity)
            .Any(r => r.TaskId == taskId && r.ReminderAt == reminderAt);

        if (!alreadyThere)
        {
            db.ReminderShown.Add(new ReminderShown(taskId, reminderAt, shownAt));
        }
    }
}
