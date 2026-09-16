using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Review;

namespace Marshal.Application.Review;

/// <summary>Jedna pozycja do rozpatrzenia w kroku kreatora.</summary>
public sealed record ReviewItem(Guid Id, string Title, string? Detail)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>
/// Kreator przeglądu tygodniowego (spec 8.3).
/// </summary>
/// <remarks>
/// Osiem kroków, każdy z licznikiem pozycji. Stan zapisywany **po każdej pojedynczej
/// pozycji**, nie po kroku — przerwanie po czterech minutach ma zostawić przegląd
/// w 30% ukończony, wznawialny dokładnie w tym miejscu, także na drugim urządzeniu.
/// </remarks>
public sealed class ReviewService(
    IReviewQueries queries,
    ITaskRepository tasks,
    IReviewSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc)
{
    /// <summary>Po ilu dniach bez ruchu projekt trafia do kroku piątego (8.3).</summary>
    private const int StaleProjectDays = 14;

    /// <summary>Po ilu dniach oczekiwania projekt uznaje się za utknięty na kimś (8.2).</summary>
    private const int StuckDays = 30;

    /// <summary>Ile dni w przód pokazuje krok siódmy (8.3).</summary>
    private const int UpcomingDays = 14;

    /// <summary>
    /// Wznawia niedokończony przegląd albo zakłada nowy.
    /// </summary>
    /// <remarks>
    /// Wznowienie jest domyślne i nie pyta. Ekran „masz niedokończony przegląd, chcesz
    /// wrócić?" byłby pytaniem, na które jest tylko jedna sensowna odpowiedź, a przy
    /// okazji dawałby okazję do zaczęcia od nowa — czyli do tego, przed czym cały ten
    /// mechanizm ma chronić.
    /// </remarks>
    public async Task<ReviewSession> StartOrResumeAsync(CancellationToken ct = default)
    {
        if (await sessions.OpenAsync(ct) is { } trwajacy)
        {
            return trwajacy;
        }

        var nowy = new ReviewSession(Guid.CreateVersion7(), clock.Now, hlc.Next());
        sessions.Add(nowy);
        await unitOfWork.SaveChangesAsync(ct);

        return nowy;
    }

    public async Task<ReviewCounts> CountsAsync(CancellationToken ct = default)
    {
        var dzis = Today();

        return new ReviewCounts(
            Inbox: await tasks.InboxCountAsync(ct),
            Overdue: (await queries.OverdueAsync(dzis, ct)).Count,
            Nudges: (await queries.WaitingAsync(dzis, ct)).Count(w => w.NeedsNudge),
            Blocked: (await queries.BlockedProjectsAsync(ct)).Count,
            StaleProjects: (await queries.StaleProjectsAsync(dzis, StaleProjectDays, ct)).Count,
            MaturedSomeday: (await queries.MaturedSomedayAsync(dzis, ct)).Count,
            Upcoming: (await tasks.UpcomingAsync(dzis, dzis.AddDays(UpcomingDays), ct)).Count,
            QuietAreas: (await queries.BalanceAsync(dzis, ct)).Count(b => b.IsQuiet));
    }

    /// <summary>
    /// Pozycje danego kroku, **bez tych już rozpatrzonych w tym przeglądzie**.
    /// </summary>
    public async Task<IReadOnlyList<ReviewItem>> ItemsAsync(
        ReviewStep step, ReviewSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var dzis = Today();

        var wszystkie = step switch
        {
            ReviewStep.Inbox => (await tasks.InboxAsync(ct))
                .Select(t => new ReviewItem(t.Id, t.Title, "w skrzynce")),

            ReviewStep.Overdue => (await queries.OverdueAsync(dzis, ct))
                .Select(t => new ReviewItem(t.Id, t.Title, $"termin {t.Deadline:yyyy-MM-dd}")),

            ReviewStep.Nudges => (await queries.WaitingAsync(dzis, ct))
                .Where(w => w.NeedsNudge)
                .Select(w => new ReviewItem(w.Task.Id, w.Task.Title, $"{w.Who}, {w.Days} dni")),

            ReviewStep.Blocked => (await queries.BlockedProjectsAsync(ct))
                .Select(p => new ReviewItem(p.ProjectId, p.Outcome, $"brak następnej akcji · {p.AreaName}"))
                .Concat((await queries.StuckOnSomeoneAsync(dzis, StuckDays, ct))
                    .Select(p => new ReviewItem(p.ProjectId, p.Outcome, $"utknięty na: {p.Who}, {p.Days} dni"))),

            ReviewStep.StaleProjects => (await queries.StaleProjectsAsync(dzis, StaleProjectDays, ct))
                .Select(p => new ReviewItem(p.Id, p.Outcome, "nietknięty od dwóch tygodni")),

            ReviewStep.Someday => (await queries.MaturedSomedayAsync(dzis, ct))
                .Select(t => new ReviewItem(t.Id, t.Title, $"odłożone do {t.DeferUntil:yyyy-MM-dd}")),

            ReviewStep.Upcoming => (await tasks.UpcomingAsync(dzis, dzis.AddDays(UpcomingDays), ct))
                .Select(t => new ReviewItem(
                    t.Id, t.Title, $"{t.DoDate ?? t.Deadline:yyyy-MM-dd}")),

            ReviewStep.Counters => (await queries.CounterFlagsAsync(dzis, ct))
                .Select(f => new ReviewItem(f.Task.Id, f.Task.Title, f.Question)),

            // Krok zerowy i krok równowagi nie mają pozycji do odhaczenia: pierwszy jest
            // tekstem do przeczytania, drugi tabelą do obejrzenia. To jest celowe — patrz
            // 8.3 i 8.5. Nie każdy krok przeglądu kończy się czynnością.
            _ => [],
        };

        return wszystkie.Where(i => !session.IsProcessed(i.Id)).ToList();
    }

    public async Task MarkProcessedAsync(
        ReviewSession session, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.MarkProcessed(id, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task GoToAsync(ReviewSession session, int step, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.GoTo(step, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task CompleteAsync(ReviewSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.Complete(clock.Now, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.Now.DateTime);
}
