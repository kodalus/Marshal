using Marshal.Domain.Diagnostics;

namespace Marshal.Application.Abstractions;

/// <summary>
/// Dziennik tego, co aplikacja zrobiła (spec 12).
/// </summary>
/// <remarks>
/// Zapis nigdy nie przerywa operacji, którą opisuje: awaria dziennika nie może być
/// gorsza od braku dziennika. Nieudane zapisy są liczone i widać je na ekranie —
/// inaczej wprowadzilibyśmy dokładnie tę cichą awarię, którą dziennik ma tępić.
/// </remarks>
public interface IActivityLog
{
    /// <summary>Ile wpisów nie udało się zapisać od uruchomienia aplikacji.</summary>
    int Dropped { get; }

    Task RecordAsync(
        string operation,
        string outcome,
        ActivityLevel level = ActivityLevel.Ok,
        string? detail = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ActivityEntry>> RecentAsync(int count = 200, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
