using Marshal.Application.Abstractions;
using Marshal.Application.Review;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Review;

public sealed class ReviewQueries(MarshalDbContext db, IDbQueue? queue = null)
    : IReviewQueries
{
    // Każde zapytanie osobno przez bramę, a nie cała metoda: przegląd składa się
    // z kilku odczytów, między którymi i tak nic nie trzyma, a chodzi o to, żeby
    // żadne z nich nie ruszyło w chwili, gdy kontekst robi co innego.
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    /// <summary>Stany, w których zadanie jest **akcją do przodu**.</summary>
    private static readonly TaskState[] Forward =
        [TaskState.Next, TaskState.Scheduled, TaskState.Waiting];

    public async Task<IReadOnlyList<BlockedProject>> BlockedProjectsAsync(
        CancellationToken ct = default)
    {
        var areas = await _queue.RunAsync(() => db.Areas.AsNoTracking()
            .Where(a => !a.Deleted)
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct), ct);

        var blocked = await _queue.RunAsync(() => db.Projects.AsNoTracking()
            .Where(p => p.State == ProjectState.Active && !p.Deleted)

            // Brak własnej akcji do przodu.
            .Where(p => !db.Tasks.Any(t =>
                t.ProjectId == p.Id && !t.Deleted && Forward.Contains(t.State)))

            // I brak żywego podprojektu, który tę akcję ma. Warunek konieczny od chwili
            // wprowadzenia zagnieżdżania (5.4): cel zwykle nie ma własnych zadań, tylko
            // podprojekty, które je mają. Bez tego każdy cel zgłaszałby się jako
            // zablokowany, N1 sypałby fałszywymi alarmami i przestałabyś na niego patrzeć.
            // Niezmiennik, który krzyczy bez powodu, jest gorszy od braku niezmiennika.
            .Where(p => !db.Projects.Any(c =>
                c.ParentProjectId == p.Id && !c.Deleted && c.State == ProjectState.Active))

            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct), ct);

        return blocked
            .Select(p => new BlockedProject(
                p.Id, p.Outcome, p.AreaId, areas.GetValueOrDefault(p.AreaId, "—")))
            .ToList();
    }

    public async Task<IReadOnlyList<StuckOnSomeone>> StuckOnSomeoneAsync(
        DateOnly today, int days, CancellationToken ct = default)
    {
        // Projekt „utknięty na kimś" to taki, w którym **każda** akcja do przodu jest
        // oczekiwaniem, i to oczekiwaniem starym. Projekt z choćby jedną akcją własną
        // nie utknął — ma co robić.
        var actions = await _queue.RunAsync(() => db.Tasks.AsNoTracking()
            .Where(t => !t.Deleted && t.ProjectId != null && Forward.Contains(t.State))
            .Select(t => new { t.ProjectId, t.State, t.WaitingSince, t.WaitingForWho })
            .ToListAsync(ct), ct);

        var projects = await _queue.RunAsync(() => db.Projects.AsNoTracking()
            .Where(p => p.State == ProjectState.Active && !p.Deleted)
            .ToDictionaryAsync(p => p.Id, p => p.Outcome, ct), ct);

        return actions
            .GroupBy(t => t.ProjectId!.Value)
            .Where(g => projects.ContainsKey(g.Key))
            .Where(g => g.All(t => t.State == TaskState.Waiting))
            .Select(g => new
            {
                Id = g.Key,
                Days = g.Min(t => Elapsed(t.WaitingSince, today)),
                Who = g.OrderBy(t => t.WaitingSince).First().WaitingForWho ?? "—",
            })
            .Where(x => x.Days >= days)
            .OrderByDescending(x => x.Days)
            .Select(x => new StuckOnSomeone(x.Id, projects[x.Id], x.Days, x.Who))
            .ToList();
    }

    public async Task<IReadOnlyList<WaitingItem>> WaitingAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var thresholds = await _queue.RunAsync(() => db.Areas.AsNoTracking()
            .Where(a => !a.Deleted)
            .ToDictionaryAsync(a => a.Id, a => a.DefaultNudgeDays, ct), ct);

        var waiting = await _queue.RunAsync(() => db.Tasks
            .Where(t => t.State == TaskState.Waiting && !t.Deleted)
            .ToListAsync(ct), ct);

        // Próg zadania nadpisuje próg obszaru (5.2). Brak obu znaczy, że obszar zniknął —
        // wtedy wartość wyjściowa, bo pozycja bez progu nigdy by się nie zgłosiła
        // i przepadłaby po cichu.
        int Threshold(TaskItem t)
        {
            if (t.WaitingNudgeDays is { } own)
            {
                return own;
            }

            var areaThreshold = t.AreaId is { } area ? thresholds.GetValueOrDefault(area) : 0;
            return areaThreshold > 0 ? areaThreshold : Area.DefaultNudgeDaysValue;
        }

        return waiting
            .Select(t => new WaitingItem(t, Elapsed(t.WaitingSince, today), Threshold(t)))
            .OrderByDescending(w => w.Days)
            .ToList();
    }

    public async Task<IReadOnlyList<AreaBalance>> BalanceAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var areas = await _queue.RunAsync(() => db.Areas.AsNoTracking()
            .Where(a => a.IsActive && !a.Deleted)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct), ct);

        var projects = await _queue.RunAsync(() => db.Projects.AsNoTracking()
            .Where(p => !p.Deleted)
            .Select(p => new { p.AreaId, p.State, p.UpdatedAt })
            .ToListAsync(ct), ct);

        // Znaczniki ściągane w całości i grupowane w pamięci. Świadomy kompromis przy
        // tej skali: kilka tysięcy wierszy to ułamek sekundy, a zegar logiczny jest
        // w bazie tekstem po konwerterze, więc liczenie maksimum po stronie bazy
        // zależałoby od tego, jak dana wersja EF przetłumaczy agregat na typ
        // konwertowany. Do zmiany, gdy tabela urośnie na tyle, że to będzie widać.
        var tasks = await _queue.RunAsync(() => db.Tasks.AsNoTracking()
            .Where(t => !t.Deleted && t.AreaId != null)
            .Select(t => new { t.AreaId, t.UpdatedAt })
            .ToListAsync(ct), ct);

        var activity = projects
            .Select(p => (Area: p.AreaId, p.UpdatedAt))
            .Concat(tasks.Select(t => (Area: t.AreaId!.Value, t.UpdatedAt)))
            .GroupBy(x => x.Area)
            .ToDictionary(g => g.Key, g => g.Max(x => x.UpdatedAt.WallMs));

        return areas
            .Select(a => new AreaBalance(
                a.Id,
                a.Name,
                projects.Count(p => p.AreaId == a.Id && p.State == ProjectState.Active),
                activity.TryGetValue(a.Id, out var when) ? DaysSince(when, today) : null,
                a.QuietDays))

            // Cisza malejąco — najdłużej milczące na górze. Bez ocen, bez czerwieni
            // i bez sugestii wyrównywania (8.5): sama liczba dni niesie komplet
            // informacji, a etykieta dokłada do niej wstyd, który nie pomaga działać.
            .OrderByDescending(b => b.DaysSinceMove ?? int.MaxValue)
            .ToList();
    }

    public async Task<IReadOnlyList<TaskItem>> OverdueAsync(
        DateOnly today, CancellationToken ct = default) =>
        await _queue.RunAsync(() => Open()
            .Where(t => t.Deadline != null && t.Deadline < today)
            .OrderBy(t => t.Deadline)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> MaturedSomedayAsync(
        DateOnly today, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Tasks
            .Where(t => t.State == TaskState.Someday && !t.Deleted
                     && t.DeferUntil != null && t.DeferUntil <= today)
            .OrderBy(t => t.DeferUntil)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Project>> StaleProjectsAsync(
        DateOnly today, int days, CancellationToken ct = default)
    {
        var active = await _queue.RunAsync(() => db.Projects
            .Where(p => p.State == ProjectState.Active && !p.Deleted)
            .ToListAsync(ct), ct);

        return active
            .Where(p => DaysSince(p.UpdatedAt.WallMs, today) >= days)
            .OrderByDescending(p => DaysSince(p.UpdatedAt.WallMs, today))
            .ToList();
    }

    public async Task<IReadOnlyList<CounterFlag>> CounterFlagsAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var open = await _queue.RunAsync(() => Open()
            .Where(t => t.RollCount >= 4 || t.FocusMissCount >= 4 || t.CarriedSince != null)
            .ToListAsync(ct), ct);

        var flags = new List<CounterFlag>();

        foreach (var task in open)
        {
            // N12: przenoszone dłużej niż trzydzieści dni.
            if (task.CarriedSince is { } od && today.DayNumber - od.DayNumber > 30)
            {
                flags.Add(new CounterFlag(
                    task,
                    $"zaległe od {od:yyyy-MM-dd} — rytm do zmiany czy do skasowania?"));
                continue;
            }

            // N15: przesunięte cztery razy.
            if (task.RollCount >= 4)
            {
                flags.Add(new CounterFlag(
                    task,
                    $"przesunięte {task.RollCount} razy — czy to jest prawdziwe zadanie?"));
                continue;
            }

            // N13: wybierane co tydzień i nierobione.
            if (task.FocusMissCount >= 4)
            {
                flags.Add(new CounterFlag(
                    task,
                    $"wybrane na dziś {task.FocusMissCount} razy i nierobione — czy to nie jest projekt?"));
            }
        }

        return flags;
    }

    public async Task<int> StaleInboxCountAsync(DateOnly today, CancellationToken ct = default)
    {
        var inbox = await _queue.RunAsync(() => db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.Inbox && !t.Deleted)
            .Select(t => t.CreatedAt)
            .ToListAsync(ct), ct);

        return inbox.Count(c => DaysSince(c.ToUnixTimeMilliseconds(), today) > 7);
    }

    private IQueryable<TaskItem> Open() =>
        db.Tasks.Where(t => !t.Deleted
                         && t.State != TaskState.Done
                         && t.State != TaskState.Trashed
                         && t.State != TaskState.Inbox);

    private static int Elapsed(DateOnly? since, DateOnly today) =>
        since is { } od ? Math.Max(0, today.DayNumber - od.DayNumber) : 0;

    /// <summary>
    /// Dni od chwili zapisanej w zegarze logicznym.
    /// </summary>
    /// <remarks>
    /// Liczone w czasie uniwersalnym, nie miejscowym: chwila pochodzi z urządzenia,
    /// które zmianę zrobiło, i jego strefy nie znamy. Przy mierze „ile dni bez ruchu"
    /// kilka godzin różnicy nie ma znaczenia, a udawanie dokładności, której nie ma,
    /// miałoby.
    /// </remarks>
    private static int DaysSince(long wallMs, DateOnly today)
    {
        var when = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(wallMs).UtcDateTime);
        return Math.Max(0, today.DayNumber - when.DayNumber);
    }
}
