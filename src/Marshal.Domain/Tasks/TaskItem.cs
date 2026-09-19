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

    /// <summary>
    /// Godzina, o której zadanie ma się odbyć. **Tylko jeśli dzień ma sens godzinowy.**
    /// </summary>
    /// <remarks>
    /// Puste jest normą, nie brakiem. Większość zadań nie ma godziny i wymuszanie jej
    /// zamieniłoby listę w kalendarz, w którym wszystko jest umówione — a to jest
    /// dokładnie ten rodzaj planowania, który się nie utrzymuje.
    /// </remarks>
    public TimeOnly? DoTime { get; private set; }

    /// <summary>Niewidoczne przed tą datą.</summary>
    public DateOnly? DeferUntil { get; private set; }

    public string? WaitingForWho { get; private set; }

    public DateOnly? WaitingSince { get; private set; }

    /// <summary>Puste = weź <c>Area.DefaultNudgeDays</c>.</summary>
    public int? WaitingNudgeDays { get; private set; }

    /// <summary>
    /// Chwila przypomnienia. Chwila, nie dzień — o to właśnie chodzi w przypomnieniu.
    /// </summary>
    /// <remarks>
    /// Osobna od <see cref="DoDate"/> i od <see cref="Deadline"/>, bo znaczy co innego niż
    /// obie: „kiedy chcę o tym usłyszeć". Zadanie na wtorek może chcieć przypomnienia
    /// w poniedziałek wieczorem, a zadanie z terminem za miesiąc — na tydzień przed.
    /// </remarks>
    public DateTimeOffset? ReminderAt { get; private set; }

    /// <summary>Waga, bez limitu (spec 1.6).</summary>
    public Priority Priority { get; private set; } = Priority.None;

    // --- wybór na dziś i widok „Teraz" (spec 8.1, 8.6) ------------------------

    /// <summary>
    /// Ile to zajmie. Puste znaczy „nieoszacowane" — i wtedy zadanie **nigdy** nie
    /// trafia do „Teraz" (spec 8.1).
    /// </summary>
    /// <remarks>
    /// To nie jest surowość, tylko warunek działania: widok, który dobiera zadania pod
    /// dostępne minuty, nie ma jak ocenić czegoś bez oszacowania. Zamiast zgadywać,
    /// ekran prosi o oszacowanie pięciu zadań, gdy kandydatów robi się mało.
    /// </remarks>
    public int? EstimatedMinutes { get; private set; }

    public Energy Energy { get; private set; } = Energy.Unknown;

    /// <summary>
    /// Dzień, na który zadanie wybrano. **Najwyżej pięć na dobę** (N14).
    /// </summary>
    /// <remarks>
    /// To nie jest <see cref="DoDate"/>. Dzień wykonania może mieć dwadzieścia zadań
    /// i ekran „Dzisiaj" pokaże dwadzieścia; wybór na dziś to te pięć, na które się
    /// piszesz. Limit działa tu bez oporu, a na wadze działałby źle, bo dotyczy jednego
    /// dnia, zeruje się co dobę i wypada przy porannym planowaniu, a nie przy wrzucaniu.
    /// </remarks>
    public DateOnly? FocusDate { get; private set; }

    /// <summary>
    /// Ile razy zadanie było wybrane na dany dzień i niewykonane (N13).
    /// </summary>
    /// <remarks>
    /// Licznik pracuje **po cichu**. Nie ma ekranu podsumowania ani „wykonano 2 z 5";
    /// niewykonany wybór po prostu wygasa. Po czwartym razie przegląd zadaje pytanie,
    /// bo zwykle odpowiedź brzmi „to nie jest jedno zadanie, tylko projekt".
    /// </remarks>
    public int FocusMissCount { get; private set; }

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
    /// <remarks>
    /// <para>
    /// Odczyt zapamiętany **pod tekstem, z którego powstał**, a nie pod flagą „już
    /// rozłożone". Flaga kłamie, gdy <see cref="RecurrenceJson"/> zmieni się z boku:
    /// scalanie (spec 9.4) i wgranie kopii (12) wpisują wartość wprost do właściwości,
    /// omijając <see cref="SetRecurrence"/>. Przy pojedynczym kontekście bazy zadanie
    /// zostaje śledzone przez całe życie aplikacji, więc stara reguła wisiałaby
    /// w pamięci do ponownego uruchomienia — a przejście dnia rodziłoby wystąpienia
    /// w rytmie, który już nie obowiązuje.
    /// </para>
    /// </remarks>
    public RecurrenceRule? Recurrence
    {
        get
        {
            if (_recurrenceFor != RecurrenceJson)
            {
                _recurrence = RecurrenceRule.FromJson(RecurrenceJson);
                _recurrenceFor = RecurrenceJson;
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

    /// <summary>Tekst, dla którego <see cref="_recurrence"/> jest aktualne.</summary>
    private string? _recurrenceFor;

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

    /// <summary>
    /// Wyprzedzenia przypomnień w minutach, zapisane jednym tekstem.
    /// </summary>
    /// <remarks>
    /// Jedną kolumną, nie tabelą potomną. Wyprzedzenia to garść małych liczb, które
    /// zmienia się zawsze wszystkie naraz — przy takim kształcie tabela dokłada złączenie
    /// i osobne wiersze do scalania, a nie daje nic w zamian. Scalanie działa per pole
    /// (9.4), więc zestaw zmieniony na telefonie zastępuje ten z komputera w całości,
    /// i tak właśnie ma być: „przypomnij mi kwadrans i dobę wcześniej" jest jedną decyzją,
    /// a nie dwiema. Ten sam zabieg co przy regule powtarzania.
    /// </remarks>
    public string? ReminderLeadsCsv { get; private set; }

    /// <summary>
    /// Ile minut przed godziną zadania ma się odezwać. Zero znaczy „o czasie".
    /// </summary>
    public IReadOnlyList<int> ReminderLeads =>
        string.IsNullOrWhiteSpace(ReminderLeadsCsv)
            ? []
            : ReminderLeadsCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => int.TryParse(w, out var minute) ? minute : -1)
                .Where(m => m >= 0)
                .Distinct()
                .OrderBy(m => m)
                .ToList();

    /// <summary>
    /// Ustawienie zestawu wyprzedzeń.
    /// </summary>
    /// <remarks>
    /// Porządkowane i odsiewane z powtórzeń przy zapisie, nie przy odczycie: dwa razy
    /// „kwadrans wcześniej" to jedno przypomnienie, a nie dwa, i lepiej, żeby wynikało
    /// to z tego, co leży w bazie, niż z tego, kto akurat czyta.
    /// </remarks>
    public void SetReminderLeads(IEnumerable<int> minutes, Hlc stamp)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        var sorted = minutes.Where(m => m >= 0).Distinct().OrderBy(m => m).ToList();

        ReminderLeadsCsv = sorted.Count == 0 ? null : string.Join(',', sorted);
        Touch(stamp);
    }

    public void SetReminder(DateTimeOffset? at, Hlc stamp)
    {
        ReminderAt = at;
        Touch(stamp);
    }

    public void SetDeadline(DateOnly? deadline, Hlc stamp)
    {
        Deadline = deadline;
        Touch(stamp);
    }

    /// <summary>Godzina wykonania. Bez dnia nie znaczy nic, więc jest wtedy czyszczona.</summary>
    public void SetDoTime(TimeOnly? time, Hlc stamp)
    {
        DoTime = DoDate is null ? null : time;
        Touch(stamp);
    }

    public void SetEstimate(int? minutes, Energy energy, Hlc stamp)
    {
        if (minutes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "Oszacowanie musi być dodatnie.");
        }

        EstimatedMinutes = minutes;
        Energy = energy;
        Touch(stamp);
    }

    /// <summary>Wybór na dany dzień. Limit pięciu pilnuje warstwa wyżej (N14).</summary>
    public void Focus(DateOnly date, Hlc stamp)
    {
        FocusDate = date;
        Touch(stamp);
    }

    /// <summary>Zdjęcie z wyboru **bez żadnej innej zmiany** (spec 8.6).</summary>
    /// <remarks>
    /// Świadomie nie dotyka licznika: zadanie zdjęte rano, żeby zrobić miejsce innemu,
    /// nie jest zadaniem, którego nie zrobiłaś. Licznik podbija dopiero koniec dnia.
    /// </remarks>
    public void Unfocus(Hlc stamp)
    {
        FocusDate = null;
        Touch(stamp);
    }

    /// <summary>Koniec dnia: wybór wygasa, licznik rośnie (spec 8.6, N13).</summary>
    public void MissFocus(Hlc stamp)
    {
        FocusDate = null;
        FocusMissCount++;
        Touch(stamp);
    }

    public void SetRecurrence(RecurrenceRule? rule, Hlc stamp)
    {
        RecurrenceJson = rule?.ToJson();
        _recurrence = rule;
        _recurrenceFor = RecurrenceJson;
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
        var next = new TaskItem(Guid.CreateVersion7(), now, stamp, Title)
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

            // Oszacowanie przechodzi — to ta sama robota. Wybór na dziś i licznik
            // pominięć nie: dotyczą konkretnego dnia i konkretnego wystąpienia.
            EstimatedMinutes = EstimatedMinutes,
            Energy = Energy,

            // Godzina przechodzi: „śmieci w poniedziałek o 19" to ta sama pora
            // w każdy poniedziałek.
            DoTime = DoTime,
        };

        // Termin przenosi się z zachowaniem odstępu od daty wykonania: „zapłacić do 10-go"
        // przy racie robionej 5-go to pięć dni zapasu, co miesiąc tyle samo. Skopiowany
        // wprost byłby od razu przeterminowany (N5) i N5 zacząłby kłamać.
        if (Deadline is { } deadline && DoDate is { } planned)
        {
            next.Deadline = doDate.AddDays(deadline.DayNumber - planned.DayNumber);
        }

        // Przypomnienie tak samo: „w przeddzień o dwudziestej" ma zostać przeddniem
        // o dwudziestej, a nie przenieść się co do daty i odezwać się natychmiast.
        if (ReminderAt is { } reminder && DoDate is { } day)
        {
            next.ReminderAt = reminder.AddDays(doDate.DayNumber - day.DayNumber);
        }

        next.SetRecurrence(rule, stamp);
        return next;
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

        // Godzina bez dnia nie znaczy nic. Zostawiona wróciłaby przy następnym
        // zaplanowaniu jako godzina, której nikt nie wybierał.
        DoTime = null;
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

    /// <summary>
    /// Kalendarz, w którym to zadanie jest widoczne dla innych. Puste = nigdzie.
    /// </summary>
    /// <remarks>
    /// Udostępnianie jest **per zadanie**, nie per obszar, i to jest rozstrzygnięcie,
    /// nie wygoda. Nie wszystko z obszaru rodzinnego ma trafiać do wspólnego kalendarza:
    /// „kupić prezent" należy do tego samego obszaru co „odebrać dziecko", a widzieć
    /// je ma tylko jedna z tych dwóch osób. Powiązanie po obszarze zmuszałoby do
    /// przenoszenia zadań między obszarami po to, żeby sterować widocznością — czyli
    /// do psucia podziału odpowiedzialności w celu, do którego nie służy.
    /// </remarks>
    public Guid? SharedCalendarId { get; private set; }

    /// <summary>Identyfikator odpowiadającego wydarzenia u źródła.</summary>
    public string? SharedEventId { get; private set; }

    public bool IsShared => SharedCalendarId is not null;

    /// <summary>Zapamiętanie, gdzie to zadanie żyje jako wydarzenie.</summary>
    public void Share(Guid calendarId, string eventId, Hlc stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        SharedCalendarId = calendarId;
        SharedEventId = eventId;
        Touch(stamp);
    }

    /// <summary>Zapomnienie o powiązaniu. Samo wydarzenie kasuje warstwa wyżej.</summary>
    public void Unshare(Hlc stamp)
    {
        SharedCalendarId = null;
        SharedEventId = null;
        Touch(stamp);
    }

    /// <summary>
    /// Wyjęcie z „kiedyś-może” z powrotem do rzeczy do zrobienia.
    /// </summary>
    /// <remarks>
    /// Wzięcie czegoś na dziś **jest** decyzją, że to już nie jest „kiedyś”. Bez tego
    /// przejścia zadanie dostawało datę wyboru, ale zostawało w stanie, którego reszta
    /// aplikacji świadomie nie pokazuje — w „Teraz” nie było go widać, choć zostało
    /// wybrane na dziś. Data odroczenia znika razem ze stanem: jest obietnicą, żeby
    /// do tego nie wracać przed czasem, a właśnie się do tego wróciło.
    /// </remarks>
    public void Activate(Hlc stamp)
    {
        if (State != TaskState.Someday)
        {
            return;
        }

        if (AreaId is not { } area)
        {
            throw new InvalidOperationException(
                "Zadanie z kiedyś-może bez obszaru — nie da się go uczynić następną akcją (N11).");
        }

        DeferUntil = null;
        MakeNext(area, stamp);
    }

    public void Complete(DateTimeOffset now, Hlc stamp)
    {
        State = TaskState.Done;
        CompletedAt = now;

        // Zdjęte z wyboru przy odhaczeniu, żeby koniec dnia nie policzył go jako
        // nierobionego. Bez tego N13 liczyłby wykonane zadania jako pominięte.
        FocusDate = null;
        Touch(stamp);
    }

    /// <summary>
    /// Zdjęcie ptaszka — zadanie znowu jest do zrobienia.
    /// </summary>
    /// <remarks>
    /// Wraca tam, skąd przyszło: z dniem wykonania do zaplanowanych, bez dnia do
    /// następnych akcji, bez obszaru do skrzynki. Stałe „następne" gubiłoby datę
    /// w tym sensie, że przestawałaby cokolwiek znaczyć — zadanie miałoby dzień
    /// i nie byłoby zaplanowane, czyli stan, którego N8 zabrania.
    ///
    /// Godzina zostaje. Przy odhaczeniu zadania bez godziny wpisuje się porę, o której
    /// naprawdę się skończyło — i jest to jedyny zapis tego, że to się w ogóle działo.
    /// Kasowanie go przy zdjęciu ptaszka usuwałoby fakt, żeby cofnąć decyzję.
    /// </remarks>
    public void Reopen(Hlc stamp)
    {
        if (State != TaskState.Done)
        {
            throw new InvalidOperationException("Otworzyć na nowo można tylko zadanie wykonane.");
        }

        State = AreaId is null
            ? TaskState.Inbox
            : DoDate is null ? TaskState.Next : TaskState.Scheduled;

        CompletedAt = null;
        Touch(stamp);
    }

    /// <summary>
    /// Przesunięcie samego dnia wykonania, bez dotykania stanu.
    /// </summary>
    /// <remarks>
    /// Dla zadania już odhaczonego. Zwykłe nadanie dnia idzie przez przejście stanu
    /// (N8), więc przeciągnięcie wykonanego bloku po siatce **wskrzeszało go**:
    /// ptaszek znikał, bo <c>Scheduled</c> nadpisywało <c>Done</c>. Przesunięcie bloku
    /// jest poprawianiem zapisu o przeszłości, a nie cofaniem decyzji o wykonaniu —
    /// od cofania jest zdjęcie ptaszka i ma być widoczne jako osobna czynność.
    /// </remarks>
    public void MoveDoDate(DateOnly? doDate, Hlc stamp)
    {
        DoDate = doDate;
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
        DoTime = null;
        DeferUntil = null;
        CarriedSince = null;
        RollCount = 0;
        ReminderAt = null;
        FocusDate = null;
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
