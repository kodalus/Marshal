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
    /// Wszystkie żywe zadania — wejście filtrów łączonych (spec 11.5).
    /// </summary>
    /// <remarks>
    /// Świadomie bez warunku: warunki filtra są sprawdzane w pamięci, nie w zapytaniu.
    /// Zob. <see cref="UseCases.FilterService"/> — tam jest napisane dlaczego.
    /// </remarks>
    Task<IReadOnlyList<TaskItem>> AllAsync(CancellationToken ct = default);

    /// <summary>
    /// Zadania otwarte z dniem wykonania w przeszłości — wejście przejścia dnia (8.4, 8.7).
    /// </summary>
    Task<IReadOnlyList<TaskItem>> OverdueByDoDateAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>Zadania otwarte z chwilą przypomnienia nie późniejszą niż podana.</summary>
    /// <summary>
    /// Zadania, które mają cokolwiek do przypomnienia — z godziną albo z chwilą.
    /// </summary>
    /// <remarks>
    /// Chwile liczy się w pamięci, nie zapytaniem: wyprzedzenia leżą w bazie jednym
    /// tekstem, a godzina zadania jest lokalna i wymaga strefy, której baza nie zna.
    /// Zadań z przypomnieniem są dziesiątki, nie tysiące, więc koszt jest żaden.
    /// </remarks>
    Task<IReadOnlyList<TaskItem>> WithRemindersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> DueRemindersAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Zadania wybrane na dany dzień (spec 8.6).</summary>
    Task<IReadOnlyList<TaskItem>> ByFocusDateAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Zadania wybrane na którykolwiek dzień z zakresu — do kalendarza.
    /// </summary>
    /// <remarks>
    /// Osobno od <c>UpcomingAsync</c>, a nie przez dopisanie warunku do niego: tamto
    /// odpowiada na pytanie „co jest umówione na te dni" i pyta o nie także przegląd
    /// tygodniowy. Wybór na dziś nie jest umówieniem — jest obietnicą daną sobie rano
    /// i policzony razem z terminami zmieniłby liczbę, którą przegląd pokazuje.
    /// </remarks>
    Task<IReadOnlyList<TaskItem>> FocusedBetweenAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>Wybory z dni minionych, niewykonane — do wygaszenia przy przejściu dnia.</summary>
    Task<IReadOnlyList<TaskItem>> ExpiredFocusAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>
    /// Zadania wyrzucone, które wciąż wskazują na swoje odbicie w kalendarzu.
    /// </summary>
    /// <remarks>
    /// To jest <b>zaległe kasowanie zapisane w bazie</b>, a nie osobna kolejka.
    /// Wyrzucenie zadania prosi kalendarz o zdjęcie odbicia, a wskazanie znika dopiero
    /// wtedy, gdy zdjęcie się udało — więc para „wyrzucone, a wskazuje" znaczy dokładnie
    /// jedno: prośba nie doszła do skutku. Sieć padła, aplikacja się zamknęła, cokolwiek.
    /// </remarks>
    Task<IReadOnlyList<TaskItem>> PendingMirrorRemovalsAsync(CancellationToken ct = default);

    /// <summary>
    /// Identyfikatory wszystkich odbić, na które wskazuje jakiekolwiek zadanie.
    /// </summary>
    /// <remarks>
    /// Bez względu na stan zadania i na to, czy wypada w oglądanym zakresie. Wskazanie
    /// znaczy „to wydarzenie jest cieniem zadania" i przestaje znaczyć dopiero wtedy,
    /// gdy zdjęcie odbicia doszło do skutku. Siatka rysuje po tym, żeby cień nie stał
    /// się na chwilę osobnym wpisem, kiedy zadanie przestaje być widoczne.
    /// </remarks>
    /// <summary>
    /// Zadania niosące rytm — po jednym na serię.
    /// </summary>
    /// <remarks>
    /// Regułę nosi zawsze najnowsze wystąpienie serii, więc takich zadań jest tyle, ile
    /// rytmów, a nie tyle, ile powtórzeń. Osobno od „co jest umówione na te dni", bo
    /// wystąpienie niosące regułę bywa **poza** oglądanym zakresem: rytm zaczepiony na
    /// dzisiaj ma się rysować także wtedy, gdy patrzy się na przyszły miesiąc.
    /// </remarks>
    Task<IReadOnlyList<TaskItem>> RecurringAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> MirroredEventIdsAsync(CancellationToken ct = default);

    void Add(TaskItem task);
}
