using Marshal.Application.Repositories;
using Marshal.Domain.Sync;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class ReminderLog(MarshalDbContext db) : IReminderLog
{
    public async Task<bool> WasShownAsync(
        Guid taskId, DateTimeOffset reminderAt, CancellationToken ct = default)
    {
        var wpis = await Find(taskId, ct);

        // Porównanie z zapisaną chwilą, nie samo istnienie wpisu: przesunięcie
        // przypomnienia ma je odblokować. Bez tego „przypomnij mi jednak o godzinę
        // później" milczałoby, bo o tym zadaniu już raz było.
        return wpis is not null && wpis.ReminderAt == reminderAt;
    }

    public void Record(Guid taskId, DateTimeOffset reminderAt, DateTimeOffset shownAt)
    {
        var istniejacy = db.ChangeTracker.Entries<ReminderShown>()
                .Select(e => e.Entity)
                .FirstOrDefault(r => r.TaskId == taskId)
            ?? db.ReminderShown.FirstOrDefault(r => r.TaskId == taskId);

        if (istniejacy is null)
        {
            db.ReminderShown.Add(new ReminderShown(taskId, reminderAt, shownAt));
        }
        else
        {
            istniejacy.Update(reminderAt, shownAt);
        }
    }

    private Task<ReminderShown?> Find(Guid taskId, CancellationToken ct) =>
        db.ReminderShown.AsNoTracking().FirstOrDefaultAsync(r => r.TaskId == taskId, ct);
}
