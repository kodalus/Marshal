using Marshal.Domain.Primitives;

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
