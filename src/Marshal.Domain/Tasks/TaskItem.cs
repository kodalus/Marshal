using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;

namespace Marshal.Domain.Tasks;

/// <summary>
/// Zadanie — od wrzutu do skrzynki po wykonaną akcję (spec 5.3).
/// </summary>
/// <remarks>
/// <para>
/// Skrzynka nie jest osobną encją: pozycja w skrzynce to zadanie w stanie
/// <see cref="TaskState.Inbox"/>. Przetworzenie jest zmianą stanu, a nie konwersją
/// z utratą identyfikatora i historii — co przy synchronizacji ma znaczenie.
/// </para>
/// <para>
/// Nazwa <c>TaskItem</c>, nie <c>Task</c>: <see cref="System.Threading.Tasks.Task"/>
/// wchodzi przez niejawne importy do każdego pliku, więc encja o tej nazwie zmuszałaby
/// do kwalifikowania nazw w całym kodzie asynchronicznym.
/// </para>
/// </remarks>
public sealed class TaskItem : Entity
{
    private TaskItem()
    {
        Title = string.Empty;
    }

    private TaskItem(Guid id, DateTimeOffset createdAt, Hlc updatedAt, string title)
        : base(id, createdAt, updatedAt)
    {
        Title = NormalizeTitle(title);
        State = TaskState.Inbox;
    }

    /// <summary>
    /// Wrzut do skrzynki. **Jedyne pole to tytuł** — zasada 1.3. Obszar, projekt,
    /// termin i reszta dochodzą dopiero przy przetwarzaniu, bo formularz przy wrzucaniu
    /// zabija zbieranie, a system bez zbierania jest pusty w ciągu tygodnia.
    /// </summary>
    public static TaskItem Capture(string title, DateTimeOffset now, Hlc stamp) =>
        new(Guid.CreateVersion7(), now, stamp, title);

    public string Title { get; private set; }

    /// <summary>Markdown.</summary>
    public string? Note { get; private set; }

    public TaskState State { get; private set; }

    /// <summary>
    /// Obszar odpowiedzialności. Pusty **wyłącznie** w stanie <see cref="TaskState.Inbox"/>
    /// i <see cref="TaskState.Trashed"/> — wynika to ze zderzenia zasady 1.3 (wrzut bez pól)
    /// z niezmiennikiem N11 (obszar wymagany). Rozstrzygnięte na korzyść wrzutu: obszar
    /// nadaje się przy przetwarzaniu.
    /// </summary>
    public Guid? AreaId { get; private set; }

    public Guid? ProjectId { get; private set; }

    /// <summary>Zagnieżdżanie bez ograniczeń. Checklista to podzadania, bez osobnego mechanizmu.</summary>
    public Guid? ParentTaskId { get; private set; }

    /// <summary>Fakt zewnętrzny. Minięcie oznacza przeterminowanie (N5).</summary>
    public DateOnly? Deadline { get; private set; }

    /// <summary>Decyzja własna, kiedy się tym zajmę. Wymagane w stanie <see cref="TaskState.Scheduled"/>.</summary>
    public DateOnly? DoDate { get; private set; }

    /// <summary>Niewidoczne przed tą datą.</summary>
    public DateOnly? DeferUntil { get; private set; }

    public string? WaitingForWho { get; private set; }

    public DateOnly? WaitingSince { get; private set; }

    /// <summary>Puste = weź <c>Area.DefaultNudgeDays</c>.</summary>
    public int? WaitingNudgeDays { get; private set; }

    /// <summary>Waga, bez limitu (spec 1.6).</summary>
    public Priority Priority { get; private set; } = Priority.None;

    /// <summary>Puste = weź kolor projektu, a w dalszej kolejności obszaru.</summary>
    public string? Color { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Pozycja ręczna; wstawienie między sąsiadów to średnia ich wartości.</summary>
    public double SortOrder { get; private set; }

    /// <summary>Dla interfejsu: czy pokazać wiersz z terminem.</summary>
    public bool HasDeadline => Deadline is not null;

    // --- powtarzalność (spec 5.7, 8.4) ---------------------------------------

    /// <summary>
    /// Reguła powtarzania w postaci bazodanowej: jeden tekst JSON, jedna kolumna.
    /// </summary>
    /// <remarks>
    /// Rozłożenie reguły na osiem kolumn dałoby osiem osobnych pól w dzienniku zmian,
    /// a scalanie per pole potrafiłoby złożyć rytm z połówek dwóch różnych decyzji:
    /// dni tygodnia z telefonu i odstęp z komputera. Reguła jest **jedną decyzją**.
    /// </remarks>
    public string? RecurrenceJson { get; private set; }

    /// <summary>
    /// Reguła powtarzania albo <c>null</c>. Regułę nosi **zawsze najnowsze wystąpienie
    /// serii** — przy tworzeniu kolejnego przechodzi na nie i znika z poprzedniego.
    /// </summary>
    /// <remarks>
    /// To nie jest kosmetyka, tylko jedyna rzecz, która czyni przetwarzanie dnia
    /// powtarzalnym: zadanie bez reguły nie umie zrodzić następnika, więc przejście dnia
    /// puszczone dwa razy nie zrobi dwóch kopii. Bez tego <see cref="OnMissed.Accumulate"/>
    /// produkowałby po jednej pozycji na każde uruchomienie.
    /// </remarks>
    public RecurrenceRule? Recurrence
    {
        get
        {
            if (!_recurrenceParsed)
            {
                _recurrence = RecurrenceRule.FromJson(RecurrenceJson);
                _recurrenceParsed = true;
            }

            return _recurrence;
        }
    }

    /// <summary>
    /// Od kiedy wystąpienie jest zaległe przy <see cref="OnMissed.Carry"/>. Ustawiane
    /// raz, przy pierwszym przeniesieniu — na potrzeby oznaczenia „zaległe od X" i N12.
    /// </summary>
    public DateOnly? CarriedSince { get; private set; }

    /// <summary>
    /// Ile razy <see cref="DoDate"/> zostało przesunięte na dziś (spec 8.7, N15).
    /// </summary>
    /// <remarks>
    /// Licznik, nie kara. Po czwartym razie przegląd zadaje pytanie, czy to jest
    /// prawdziwe zadanie — zwykle odpowiedź brzmi „to nie jedno zadanie, tylko projekt".
    /// </remarks>
    public int RollCount { get; private set; }

    private RecurrenceRule? _recurrence;

    private bool _recurrenceParsed;

    public void Rename(string title, Hlc stamp)
    {
        Title = NormalizeTitle(title);
        Touch(stamp);
    }

    public void SetNote(string? note, Hlc stamp)
    {
        Note = string.IsNullOrWhiteSpace(note) ? null : note;
        Touch(stamp);
    }

    public void SetPriority(Priority priority, Hlc stamp)
    {
        Priority = priority;
        Touch(stamp);
    }

    public void SetColor(string? color, Hlc stamp)
    {
        Color = color;
        Touch(stamp);
    }

    public void SetDeadline(DateOnly? deadline, Hlc stamp)
    {
        Deadline = deadline;
        Touch(stamp);
    }

    public void SetRecurrence(RecurrenceRule? rule, Hlc stamp)
    {
        RecurrenceJson = rule?.ToJson();
        _recurrence = rule;
        _recurrenceParsed = true;
        Touch(stamp);
    }

    /// <summary>
    /// Przesunięcie zaplanowanego zadania na dziś przy przejściu dnia (spec 8.7).
    /// </summary>
    /// <remarks>
    /// Zostawienie zaległego na zawsze buduje stertę. Ciche cofnięcie do
    /// <see cref="TaskState.Next"/> gubi informację, że planowałaś i nie zrobiłaś.
    /// Przesunięcie z licznikiem zachowuje jedno i drugie.
    /// </remarks>
    public void RollTo(DateOnly today, Hlc stamp)
    {
        DoDate = today;
        RollCount++;
        Touch(stamp);
    }

    /// <summary>
    /// Przeniesienie niewykonanego wystąpienia na dziś przy <see cref="OnMissed.Carry"/>.
    /// </summary>
    public void CarryTo(DateOnly today, Hlc stamp)
    {
        // Ustawiane raz: „zaległe od 3 września" ma wskazywać pierwszy przegapiony dzień,
        // a nie wczoraj. Bez tego N12 nigdy nie doliczyłby trzydziestu dni.
        CarriedSince ??= DoDate ?? today;
        DoDate = today;
        Touch(stamp);
    }

    /// <summary>
    /// Kolejne wystąpienie serii: kopia pól, nowy identyfikator, nowa data (spec 8.4).
    /// </summary>
    /// <remarks>
    /// Kopia, a nie przestawienie daty w tym samym zadaniu. Odhaczone wystąpienie ma
    /// zostać odhaczone: historia „robiłam to w każdy poniedziałek prócz jednego" jest
    /// całą wartością powtarzalności, a zadanie wędrujące w przyszłość jej nie niesie.
    /// </remarks>
    internal TaskItem SpawnNextOccurrence(
        DateOnly doDate, RecurrenceRule rule, DateTimeOffset now, Hlc stamp)
    {
        var nastepne = new TaskItem(Guid.CreateVersion7(), now, stamp, Title)
        {
            Note = Note,
            State = TaskState.Scheduled,
            AreaId = AreaId,
            ProjectId = ProjectId,
            ParentTaskId = ParentTaskId,
            DoDate = doDate,
            Priority = Priority,
            Color = Color,
            SortOrder = SortOrder,
        };

        // Termin przenosi się z zachowaniem odstępu od daty wykonania: „zapłacić do 10-go"
        // przy racie robionej 5-go to pięć dni zapasu, co miesiąc tyle samo. Skopiowany
        // wprost byłby od razu przeterminowany (N5) i N5 zacząłby kłamać.
        if (Deadline is { } termin && DoDate is { } planowana)
        {
            nastepne.Deadline = doDate.AddDays(termin.DayNumber - planowana.DayNumber);
        }

        nastepne.SetRecurrence(rule, stamp);
        return nastepne;
    }

    /// <summary>
    /// Pominięte wystąpienie serii <see cref="OnMissed.Accumulate"/> przestaje być
    /// zaplanowane na dzień, a staje się zwykłą zaległością.
    /// </summary>
    /// <remarks>
    /// Rozstrzygnięcie sprzeczności między 8.4 a 8.7. Spec każe zostawić takie
    /// wystąpienie z pierwotną datą, ale osobno każe przesuwać na dziś każde zaplanowane
    /// zadanie z przeszłości — więc trzy nieodhaczone treningi wylądowałyby na dzisiaj,
    /// a nazajutrz znowu, i po czterech dniach każdy z nich odpaliłby N15. Mechanizm
    /// zjadałby sam siebie.
    ///
    /// Zaległe wystąpienie zostaje więc następną akcją bez wyznaczonego dnia — bo tym
    /// właśnie jest — a dzień, na który było umówione, zostaje w
    /// <see cref="CarriedSince"/> jako „zaległe od".
    /// </remarks>
    public void LeaveAsDebt(Hlc stamp)
    {
        CarriedSince ??= DoDate;
        DoDate = null;
        State = TaskState.Next;
        Touch(stamp);
    }

    public void MoveTo(Guid areaId, Guid? projectId, Hlc stamp)
    {
        RequireArea(areaId);
        AreaId = areaId;
        ProjectId = projectId;
        Touch(stamp);
    }

    public void Reparent(Guid? parentTaskId, Hlc stamp)
    {
        if (parentTaskId == Id)
        {
            throw new InvalidOperationException("Zadanie nie może być własnym podzadaniem.");
        }

        ParentTaskId = parentTaskId;
        Touch(stamp);
    }

    public void SetSortOrder(double sortOrder, Hlc stamp)
    {
        SortOrder = sortOrder;
        Touch(stamp);
    }

    // --- przejścia drzewka przetwarzania (spec, rozdz. 7) ---------------------

    /// <summary>Następna akcja bez wyznaczonego dnia.</summary>
    public void MakeNext(Guid areaId, Hlc stamp)
    {
        RequireArea(areaId);
        AreaId = areaId;
        State = TaskState.Next;
        ClearWaiting();
        Touch(stamp);
    }

    /// <summary>Zaplanowane na konkretny dzień. N8: bez <paramref name="doDate"/> nie wolno zapisać.</summary>
    public void Schedule(Guid areaId, DateOnly doDate, Hlc stamp)
    {
        RequireArea(areaId);
        AreaId = areaId;
        DoDate = doDate;
        State = TaskState.Scheduled;
        ClearWaiting();
        Touch(stamp);
    }

    /// <summary>Czekam na kogoś. N2: „kto" i „od kiedy" są wymagane.</summary>
    public void Delegate(Guid areaId, string who, DateOnly since, int? nudgeDays, Hlc stamp)
    {
        RequireArea(areaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (nudgeDays is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nudgeDays), "Próg ponaglenia musi być dodatni.");
        }

        AreaId = areaId;
        WaitingForWho = who.Trim();
        WaitingSince = since;
        WaitingNudgeDays = nudgeDays;
        State = TaskState.Waiting;
        Touch(stamp);
    }

    /// <summary>Kiedyś-może. Bez presji i bez terminu.</summary>
    public void Postpone(Guid areaId, DateOnly? deferUntil, Hlc stamp)
    {
        RequireArea(areaId);
        AreaId = areaId;
        DeferUntil = deferUntil;
        State = TaskState.Someday;
        ClearWaiting();
        Touch(stamp);
    }

    public void Complete(DateTimeOffset now, Hlc stamp)
    {
        State = TaskState.Done;
        CompletedAt = now;
        Touch(stamp);
    }

    public void Reopen(Hlc stamp)
    {
        if (State != TaskState.Done)
        {
            throw new InvalidOperationException("Otworzyć na nowo można tylko zadanie wykonane.");
        }

        State = AreaId is null ? TaskState.Inbox : TaskState.Next;
        CompletedAt = null;
        Touch(stamp);
    }

    /// <summary>Kosz. Nagrobek zostaje — usunięcia fizycznego nie ma (spec 5.1).</summary>
    public void Trash(Hlc stamp)
    {
        State = TaskState.Trashed;
        Touch(stamp);
    }

    /// <summary>Powrót do skrzynki — cofnięcie pochopnego przetworzenia.</summary>
    public void ReturnToInbox(Hlc stamp)
    {
        State = TaskState.Inbox;
        CompletedAt = null;
        DoDate = null;
        DeferUntil = null;
        CarriedSince = null;
        RollCount = 0;
        ClearWaiting();
        Touch(stamp);
    }

    private void ClearWaiting()
    {
        WaitingForWho = null;
        WaitingSince = null;
        WaitingNudgeDays = null;
    }

    private static void RequireArea(Guid areaId)
    {
        if (areaId == Guid.Empty)
        {
            throw new ArgumentException(
                "Obszar jest wymagany poza skrzynką (N11) — bez niego tabela równowagi pokazuje część życia, wyglądając na całość.",
                nameof(areaId));
        }
    }

    private static string NormalizeTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return title.Trim();
    }
}
