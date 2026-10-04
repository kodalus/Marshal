using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Habits;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class HabitRepository(MarshalDbContext db, IDbQueue? queue = null)
    : IHabitRepository
{
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    public async Task<IReadOnlyList<Habit>> ListAsync(
        bool archived = false, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Habits
            .Where(h => !h.Deleted && h.Archived == archived)
            .OrderBy(h => h.SortOrder)
            .ThenBy(h => h.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<Habit?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _queue.RunAsync(
            () => db.Habits.FirstOrDefaultAsync(h => h.Id == id && !h.Deleted, ct), ct);

    public async Task<IReadOnlyList<HabitMark>> MarksAsync(CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.HabitMarks
            .Where(m => !m.Deleted && m.Amount > 0)
            .ToListAsync(ct), ct);

    public async Task<HabitMark?> MarkAsync(
        Guid habitId, DateOnly day, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.HabitMarks
            .FirstOrDefaultAsync(m => m.HabitId == habitId && m.Day == day && !m.Deleted, ct), ct);

    public void Add(Habit habit) => db.Habits.Add(habit);

    public void Add(HabitMark mark) => db.HabitMarks.Add(mark);
}
