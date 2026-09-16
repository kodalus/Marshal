using Marshal.Domain.Tasks;

namespace Marshal.Application.Repositories;

public interface ITaskRepository
{
    Task<TaskItem?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>Skrzynka, najstarsze pierwsze — przetwarza się w kolejności wrzucania.</summary>
    Task<IReadOnlyList<TaskItem>> InboxAsync(CancellationToken ct = default);

    Task<int> InboxCountAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> ByStateAsync(TaskState state, CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> ByProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Ekran „Dzisiaj": zadania z dniem wykonania nie później niż dziś oraz te po
    /// terminie. Jedno i drugie znaczy co innego (spec 1.5), ale oba trafiają na
    /// ten sam ekran, bo oba dotyczą dzisiejszego dnia.
    /// </summary>
    Task<IReadOnlyList<TaskItem>> TodayAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>Ekran „Plany": oś czasu w przód, po dniu wykonania albo terminie.</summary>
    Task<IReadOnlyList<TaskItem>> UpcomingAsync(DateOnly after, DateOnly until, CancellationToken ct = default);

    /// <summary>Archiwum: wykonane i wyrzucone, najnowsze pierwsze.</summary>
    Task<IReadOnlyList<TaskItem>> ArchiveAsync(int limit, CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> ByAreaAsync(Guid areaId, CancellationToken ct = default);

    /// <summary>
    /// Zadania otwarte z dniem wykonania w przeszłości — wejście przejścia dnia (8.4, 8.7).
    /// </summary>
    Task<IReadOnlyList<TaskItem>> OverdueByDoDateAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>Zadania otwarte z chwilą przypomnienia nie późniejszą niż podana.</summary>
    Task<IReadOnlyList<TaskItem>> DueRemindersAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Zadania wybrane na dany dzień (spec 8.6).</summary>
    Task<IReadOnlyList<TaskItem>> ByFocusDateAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>Wybory z dni minionych, niewykonane — do wygaszenia przy przejściu dnia.</summary>
    Task<IReadOnlyList<TaskItem>> ExpiredFocusAsync(DateOnly today, CancellationToken ct = default);

    void Add(TaskItem task);
}
