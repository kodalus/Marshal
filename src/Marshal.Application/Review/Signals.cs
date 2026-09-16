using Marshal.Domain.Tasks;

namespace Marshal.Application.Review;

/// <summary>
/// Projekt bez następnej akcji (N1). Najważniejszy niezmiennik w całym dokumencie.
/// </summary>
/// <remarks>
/// W klasycznym GTD „projekt bez następnej akcji" to rzecz, którą wyłapuje się okiem
/// raz w tygodniu — albo nie wyłapuje wcale. Tutaj jest zapytaniem.
/// </remarks>
public sealed record BlockedProject(Guid ProjectId, string Outcome, Guid AreaId, string AreaName);

/// <summary>
/// Projekt, w którym wszystkie akcje czekają na kogoś dłużej niż próg (spec 8.2).
/// </summary>
/// <remarks>
/// Zgłaszany osobno od <see cref="BlockedProject"/>, bo to inny problem i inna reakcja.
/// Czekanie jest prawidłowym stanem; czekanie od trzech miesięcy jest sygnałem, że
/// trzeba ponaglić albo poszukać innej drogi.
/// </remarks>
public sealed record StuckOnSomeone(Guid ProjectId, string Outcome, int Days, string Who);

/// <summary>Zadanie oczekiwane razem z tym, ile już czeka i po ilu dniach ponaglać.</summary>
public sealed record WaitingItem(TaskItem Task, int Days, int NudgeDays)
{
    /// <summary>N3 — czas ponaglić.</summary>
    public bool NeedsNudge => Days >= NudgeDays;

    public string Who => Task.WaitingForWho ?? "—";
}

/// <summary>
/// Wiersz tabeli równowagi obszarów (spec 8.5).
/// </summary>
/// <remarks>
/// <see cref="DaysSinceMove"/> puste znaczy „brak ruchu" — obszar, w którym nigdy nic
/// się nie zapisało. To nie to samo co „ruch dawno temu" i nie wolno tego mylić,
/// pokazując na przykład zero albo bardzo dużą liczbę.
/// </remarks>
public sealed record AreaBalance(
    Guid AreaId, string Name, int ActiveProjects, int? DaysSinceMove, int QuietDays)
{
    /// <summary>N10 — obszar milczy dłużej niż własny próg.</summary>
    public bool IsQuiet => DaysSinceMove is null || DaysSinceMove >= QuietDays;
}

/// <summary>
/// Zadanie, którego licznik przekroczył próg (N12, N13, N15).
/// </summary>
/// <remarks>
/// <see cref="Question"/> jest pytaniem, nie oceną. „Przesunięte cztery razy — czy to
/// jest prawdziwe zadanie?" da się na coś zamienić; „zaniedbane" nie da się na nic.
/// </remarks>
public sealed record CounterFlag(TaskItem Task, string Question);

/// <summary>
/// Liczniki do kroków kreatora przeglądu (spec 8.3).
/// </summary>
/// <remarks>
/// Jedno przejście po bazie na cały kreator, nie jedno na krok. Kreator pokazuje
/// wszystkie liczniki naraz, żeby było widać, ile zostało — a osiem osobnych zapytań
/// przy każdym kroku zamieniłoby otwarcie przeglądu w czekanie.
/// </remarks>
public sealed record ReviewCounts(
    int Inbox,
    int Overdue,
    int Nudges,
    int Blocked,
    int StaleProjects,
    int MaturedSomeday,
    int Upcoming,
    int QuietAreas)
{
    public int Total =>
        Inbox + Overdue + Nudges + Blocked + StaleProjects + MaturedSomeday + Upcoming + QuietAreas;
}

/// <summary>
/// Pozycje, które kreator przeglądu ma pokazać w danym kroku (spec 8.3).
/// </summary>
public enum ReviewStep
{
    /// <summary>Krok zerowy: przypięte notatki. Bez żadnej akcji do wykonania.</summary>
    Pinned = 0,
    Inbox = 1,
    Overdue = 2,
    Nudges = 3,
    Blocked = 4,
    StaleProjects = 5,
    Someday = 6,
    Upcoming = 7,

    /// <summary>N12, N13, N15 — liczniki, które o coś pytają.</summary>
    Counters = 8,
    Balance = 9,
}
