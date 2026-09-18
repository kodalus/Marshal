using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class TaskRepository(MarshalDbContext db, IKolejkaBazy? kolejka = null)
    : ITaskRepository
{
    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    public async Task<TaskItem?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks.FirstOrDefaultAsync(t => t.Id == id && !t.Deleted, ct), ct);

    public async Task<IReadOnlyList<TaskItem>> InboxAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.State == TaskState.Inbox && !t.Deleted)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct), ct);

    public Task<int> InboxCountAsync(CancellationToken ct = default) =>
        _kolejka.WykonajAsync(
            () => db.Tasks.CountAsync(t => t.State == TaskState.Inbox && !t.Deleted, ct), ct);

    public async Task<IReadOnlyList<TaskItem>> ByStateAsync(TaskState state, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.State == state && !t.Deleted)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> ByProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.ProjectId == projectId && !t.Deleted && t.State != TaskState.Trashed)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct), ct);

    /// <summary>
    /// Sprawy na dziś — razem z tym, co już dziś odhaczone.
    /// </summary>
    /// <remarks>
    /// Odhaczone zadanie **zostaje na liście**, tylko z ptaszkiem. Znikające sprawiało,
    /// że dzień wyglądał na coraz bardziej pusty w miarę pracy, a zrobionego nie dało
    /// się ani zobaczyć, ani cofnąć bez chodzenia po archiwum. Zostaje do końca dnia:
    /// jutro liczy się już tylko to, co jutrzejsze.
    /// </remarks>
    public async Task<IReadOnlyList<TaskItem>> TodayAsync(DateOnly today, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => !t.Deleted
                     && t.State != TaskState.Trashed
                     && t.State != TaskState.Inbox
                     && (t.State != TaskState.Done || t.DoDate == today))
            .Where(t => (t.DoDate != null && t.DoDate <= today)
                     || (t.Deadline != null && t.Deadline <= today))
            .OrderBy(t => t.Deadline == null)
            .ThenBy(t => t.Deadline)
            .ThenBy(t => t.DoDate)
            .ToListAsync(ct), ct);

    /// <summary>
    /// Zadania w oknie dni — razem z odhaczonymi, które na te dni przypadały.
    /// </summary>
    /// <remarks>
    /// Siatka kalendarza bierze zadania stąd, a odhaczone ma pokazywać z ptaszkiem,
    /// nie usuwać. Blok znikający po odhaczeniu zabierał z dnia ślad po tym, że coś
    /// się stało — i sprawiał, że wieczorem kalendarz wyglądał na dzień, w którym
    /// nic nie było zaplanowane.
    /// </remarks>
    public async Task<IReadOnlyList<TaskItem>> UpcomingAsync(
        DateOnly after, DateOnly until, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => !t.Deleted
                     && t.State != TaskState.Trashed
                     && t.State != TaskState.Inbox)
            .Where(t => (t.DoDate != null && t.DoDate > after && t.DoDate <= until)
                     || (t.Deadline != null && t.Deadline > after && t.Deadline <= until))
            .OrderBy(t => t.DoDate ?? t.Deadline)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> ArchiveAsync(int limit, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => !t.Deleted && (t.State == TaskState.Done || t.State == TaskState.Trashed))
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> ByAreaAsync(Guid areaId, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => Otwarte()
            .Where(t => t.AreaId == areaId)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct), ct);

    /// <summary>
    /// Wszystkie żywe zadania, także wykonane i wyrzucone.
    /// </summary>
    /// <remarks>
    /// Stan odsiewa filtr, nie zapytanie: „pokaż wykonane w tym tygodniu" musi być
    /// wykonalne, a byłoby nie do ułożenia, gdyby archiwum nie dochodziło tu w ogóle.
    /// Domyślne ukrycie cmentarza siedzi w <see cref="Marshal.Domain.Filters.FilterQuery"/>,
    /// czyli w jednym miejscu, razem z powodem.
    /// </remarks>
    public async Task<IReadOnlyList<TaskItem>> AllAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => !t.Deleted)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> OverdueByDoDateAsync(
        DateOnly today, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => Otwarte()
            .Where(t => t.DoDate != null && t.DoDate < today)
            // Najstarsze pierwsze: przy Accumulate kolejność ma znaczenie, bo każde
            // wystąpienie rodzi następne i rytm musi wyjść z najdawniejszego.
            .OrderBy(t => t.DoDate)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> WithRemindersAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => Otwarte()
            .Where(t => t.ReminderAt != null || t.ReminderLeadsCsv != null)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> DueRemindersAsync(
        DateTimeOffset now, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => Otwarte()
            .Where(t => t.ReminderAt != null && t.ReminderAt <= now)
            // Najdawniejsze pierwsze: gdy uzbierało się kilka, kolejność ma być taka,
            // w jakiej miały się odezwać.
            .OrderBy(t => t.ReminderAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> ByFocusDateAsync(
        DateOnly date, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.FocusDate == date && !t.Deleted && t.State != TaskState.Trashed)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> FocusedBetweenAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.FocusDate != null && t.FocusDate >= from && t.FocusDate < to
                     && !t.Deleted && t.State != TaskState.Trashed)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> ExpiredFocusAsync(
        DateOnly today, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.FocusDate != null && t.FocusDate < today && !t.Deleted
                     && t.State != TaskState.Done && t.State != TaskState.Trashed)
            .ToListAsync(ct), ct);

    /// <summary>Zadania nierozstrzygnięte: poza skrzynką, koszem i wykonanymi.</summary>
    private IQueryable<TaskItem> Otwarte() =>
        db.Tasks.Where(t => !t.Deleted
                         && t.State != TaskState.Done
                         && t.State != TaskState.Trashed
                         && t.State != TaskState.Inbox);

    public async Task<IReadOnlyList<TaskItem>> PendingMirrorRemovalsAsync(
        CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            // Także nagrobki: zadanie skasowane na drugim urządzeniu przyjeżdża tu jako
            // nagrobek ze wskazaniem, a jego odbicie nie ma kto zdjąć poza nami.
            .Where(t => t.SharedEventId != null
                     && (t.Deleted || t.State == TaskState.Trashed))
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<string>> MirroredEventIdsAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.SharedEventId != null)
            .Select(t => t.SharedEventId!)
            .Distinct()
            .ToListAsync(ct), ct);

    public void Add(TaskItem task) => db.Tasks.Add(task);
}
