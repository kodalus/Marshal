using Marshal.Application.Abstractions;
using Marshal.Application.Review;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Review;

public sealed class ReviewQueries(MarshalDbContext db, IKolejkaBazy? kolejka = null)
    : IReviewQueries
{
    // Każde zapytanie osobno przez bramę, a nie cała metoda: przegląd składa się
    // z kilku odczytów, między którymi i tak nic nie trzyma, a chodzi o to, żeby
    // żadne z nich nie ruszyło w chwili, gdy kontekst robi co innego.
    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    /// <summary>Stany, w których zadanie jest **akcją do przodu**.</summary>
    private static readonly TaskState[] Forward =
        [TaskState.Next, TaskState.Scheduled, TaskState.Waiting];

    public async Task<IReadOnlyList<BlockedProject>> BlockedProjectsAsync(
        CancellationToken ct = default)
    {
        var obszary = await _kolejka.WykonajAsync(() => db.Areas.AsNoTracking()
            .Where(a => !a.Deleted)
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct), ct);

        var zablokowane = await _kolejka.WykonajAsync(() => db.Projects.AsNoTracking()
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

        return zablokowane
            .Select(p => new BlockedProject(
                p.Id, p.Outcome, p.AreaId, obszary.GetValueOrDefault(p.AreaId, "—")))
            .ToList();
    }

    public async Task<IReadOnlyList<StuckOnSomeone>> StuckOnSomeoneAsync(
        DateOnly today, int days, CancellationToken ct = default)
    {
        // Projekt „utknięty na kimś" to taki, w którym **każda** akcja do przodu jest
        // oczekiwaniem, i to oczekiwaniem starym. Projekt z choćby jedną akcją własną
        // nie utknął — ma co robić.
        var akcje = await _kolejka.WykonajAsync(() => db.Tasks.AsNoTracking()
            .Where(t => !t.Deleted && t.ProjectId != null && Forward.Contains(t.State))
            .Select(t => new { t.ProjectId, t.State, t.WaitingSince, t.WaitingForWho })
            .ToListAsync(ct), ct);

        var projekty = await _kolejka.WykonajAsync(() => db.Projects.AsNoTracking()
            .Where(p => p.State == ProjectState.Active && !p.Deleted)
            .ToDictionaryAsync(p => p.Id, p => p.Outcome, ct), ct);

        return akcje
            .GroupBy(t => t.ProjectId!.Value)
            .Where(g => projekty.ContainsKey(g.Key))
            .Where(g => g.All(t => t.State == TaskState.Waiting))
            .Select(g => new
            {
                Id = g.Key,
                Dni = g.Min(t => Elapsed(t.WaitingSince, today)),
                Kto = g.OrderBy(t => t.WaitingSince).First().WaitingForWho ?? "—",
            })
            .Where(x => x.Dni >= days)
            .OrderByDescending(x => x.Dni)
            .Select(x => new StuckOnSomeone(x.Id, projekty[x.Id], x.Dni, x.Kto))
            .ToList();
    }

    public async Task<IReadOnlyList<WaitingItem>> WaitingAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var progi = await _kolejka.WykonajAsync(() => db.Areas.AsNoTracking()
            .Where(a => !a.Deleted)
            .ToDictionaryAsync(a => a.Id, a => a.DefaultNudgeDays, ct), ct);

        var oczekiwane = await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.State == TaskState.Waiting && !t.Deleted)
            .ToListAsync(ct), ct);

        // Próg zadania nadpisuje próg obszaru (5.2). Brak obu znaczy, że obszar zniknął —
        // wtedy wartość wyjściowa, bo pozycja bez progu nigdy by się nie zgłosiła
        // i przepadłaby po cichu.
        int Prog(TaskItem t)
        {
            if (t.WaitingNudgeDays is { } wlasny)
            {
                return wlasny;
            }

            var obszaru = t.AreaId is { } obszar ? progi.GetValueOrDefault(obszar) : 0;
            return obszaru > 0 ? obszaru : Area.DefaultNudgeDaysValue;
        }

        return oczekiwane
            .Select(t => new WaitingItem(t, Elapsed(t.WaitingSince, today), Prog(t)))
            .OrderByDescending(w => w.Days)
            .ToList();
    }

    public async Task<IReadOnlyList<AreaBalance>> BalanceAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var obszary = await _kolejka.WykonajAsync(() => db.Areas.AsNoTracking()
            .Where(a => a.IsActive && !a.Deleted)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct), ct);

        var projekty = await _kolejka.WykonajAsync(() => db.Projects.AsNoTracking()
            .Where(p => !p.Deleted)
            .Select(p => new { p.AreaId, p.State, p.UpdatedAt })
            .ToListAsync(ct), ct);

        // Znaczniki ściągane w całości i grupowane w pamięci. Świadomy kompromis przy
        // tej skali: kilka tysięcy wierszy to ułamek sekundy, a zegar logiczny jest
        // w bazie tekstem po konwerterze, więc liczenie maksimum po stronie bazy
        // zależałoby od tego, jak dana wersja EF przetłumaczy agregat na typ
        // konwertowany. Do zmiany, gdy tabela urośnie na tyle, że to będzie widać.
        var zadania = await _kolejka.WykonajAsync(() => db.Tasks.AsNoTracking()
            .Where(t => !t.Deleted && t.AreaId != null)
            .Select(t => new { t.AreaId, t.UpdatedAt })
            .ToListAsync(ct), ct);

        var ruch = projekty
            .Select(p => (Area: p.AreaId, p.UpdatedAt))
            .Concat(zadania.Select(t => (Area: t.AreaId!.Value, t.UpdatedAt)))
            .GroupBy(x => x.Area)
            .ToDictionary(g => g.Key, g => g.Max(x => x.UpdatedAt.WallMs));

        return obszary
            .Select(a => new AreaBalance(
                a.Id,
                a.Name,
                projekty.Count(p => p.AreaId == a.Id && p.State == ProjectState.Active),
                ruch.TryGetValue(a.Id, out var kiedy) ? DaysSince(kiedy, today) : null,
                a.QuietDays))

            // Cisza malejąco — najdłużej milczące na górze. Bez ocen, bez czerwieni
            // i bez sugestii wyrównywania (8.5): sama liczba dni niesie komplet
            // informacji, a etykieta dokłada do niej wstyd, który nie pomaga działać.
            .OrderByDescending(b => b.DaysSinceMove ?? int.MaxValue)
            .ToList();
    }

    public async Task<IReadOnlyList<TaskItem>> OverdueAsync(
        DateOnly today, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => Open()
            .Where(t => t.Deadline != null && t.Deadline < today)
            .OrderBy(t => t.Deadline)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskItem>> MaturedSomedayAsync(
        DateOnly today, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tasks
            .Where(t => t.State == TaskState.Someday && !t.Deleted
                     && t.DeferUntil != null && t.DeferUntil <= today)
            .OrderBy(t => t.DeferUntil)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Project>> StaleProjectsAsync(
        DateOnly today, int days, CancellationToken ct = default)
    {
        var aktywne = await _kolejka.WykonajAsync(() => db.Projects
            .Where(p => p.State == ProjectState.Active && !p.Deleted)
            .ToListAsync(ct), ct);

        return aktywne
            .Where(p => DaysSince(p.UpdatedAt.WallMs, today) >= days)
            .OrderByDescending(p => DaysSince(p.UpdatedAt.WallMs, today))
            .ToList();
    }

    public async Task<IReadOnlyList<CounterFlag>> CounterFlagsAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var otwarte = await _kolejka.WykonajAsync(() => Open()
            .Where(t => t.RollCount >= 4 || t.FocusMissCount >= 4 || t.CarriedSince != null)
            .ToListAsync(ct), ct);

        var flagi = new List<CounterFlag>();

        foreach (var zadanie in otwarte)
        {
            // N12: przenoszone dłużej niż trzydzieści dni.
            if (zadanie.CarriedSince is { } od && today.DayNumber - od.DayNumber > 30)
            {
                flagi.Add(new CounterFlag(
                    zadanie,
                    $"zaległe od {od:yyyy-MM-dd} — rytm do zmiany czy do skasowania?"));
                continue;
            }

            // N15: przesunięte cztery razy.
            if (zadanie.RollCount >= 4)
            {
                flagi.Add(new CounterFlag(
                    zadanie,
                    $"przesunięte {zadanie.RollCount} razy — czy to jest prawdziwe zadanie?"));
                continue;
            }

            // N13: wybierane co tydzień i nierobione.
            if (zadanie.FocusMissCount >= 4)
            {
                flagi.Add(new CounterFlag(
                    zadanie,
                    $"wybrane na dziś {zadanie.FocusMissCount} razy i nierobione — czy to nie jest projekt?"));
            }
        }

        return flagi;
    }

    public async Task<int> StaleInboxCountAsync(DateOnly today, CancellationToken ct = default)
    {
        var wrzuty = await _kolejka.WykonajAsync(() => db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.Inbox && !t.Deleted)
            .Select(t => t.CreatedAt)
            .ToListAsync(ct), ct);

        return wrzuty.Count(c => DaysSince(c.ToUnixTimeMilliseconds(), today) > 7);
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
        var kiedy = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(wallMs).UtcDateTime);
        return Math.Max(0, today.DayNumber - kiedy.DayNumber);
    }
}
