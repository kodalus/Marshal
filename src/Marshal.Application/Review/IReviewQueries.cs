using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Review;

/// <summary>
/// Odczyty na potrzeby niezmienników i przeglądu (spec 6, 8.2, 8.3, 8.5).
/// </summary>
/// <remarks>
/// Osobny port od repozytoriów, bo to są **zapytania, nie agregaty**. Repozytorium
/// oddaje encje do zmiany; tutaj chodzi o liczby i zestawienia, które nikogo nie
/// zmieniają i którym wolno sięgać w poprzek agregatów.
/// </remarks>
public interface IReviewQueries
{
    /// <summary>N1: projekty aktywne bez własnej akcji do przodu i bez żywego podprojektu.</summary>
    Task<IReadOnlyList<BlockedProject>> BlockedProjectsAsync(CancellationToken ct = default);

    /// <summary>Projekty, w których wszystko czeka na kogoś dłużej niż <paramref name="days"/>.</summary>
    Task<IReadOnlyList<StuckOnSomeone>> StuckOnSomeoneAsync(
        DateOnly today, int days, CancellationToken ct = default);

    /// <summary>Wszystkie oczekiwane, najdłużej czekające pierwsze.</summary>
    Task<IReadOnlyList<WaitingItem>> WaitingAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>Tabela równowagi (8.5), posortowana po ciszy malejąco.</summary>
    Task<IReadOnlyList<AreaBalance>> BalanceAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>N5: niewykonane zadania po terminie.</summary>
    Task<IReadOnlyList<TaskItem>> OverdueAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>N6: „kiedyś-może", którym minęła data odłożenia.</summary>
    Task<IReadOnlyList<TaskItem>> MaturedSomedayAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>Projekty aktywne nietknięte od <paramref name="days"/> dni (krok 5 przeglądu).</summary>
    Task<IReadOnlyList<Project>> StaleProjectsAsync(
        DateOnly today, int days, CancellationToken ct = default);

    /// <summary>N4: ile pozycji zalega w skrzynce dłużej niż tydzień.</summary>
    Task<int> StaleInboxCountAsync(DateOnly today, CancellationToken ct = default);
}
