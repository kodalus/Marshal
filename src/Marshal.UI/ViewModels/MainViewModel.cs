using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;
using Marshal.Application.Repositories;
using Marshal.Application.Review;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Notifications;

namespace Marshal.UI.ViewModels;

public enum Screen
{
    Today,
    Now,
    Inbox,
    Clarify,
    Next,
    Plans,
    Projects,
    Someday,
    Waiting,
    Calendar,
    Notes,
    Filters,
    Settings,
    Areas,
    Archive,
    Review,

    /// <summary>Co aplikacja zrobiła i co z tego wyszło (spec 12).</summary>
    Journal,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly InboxService _inbox;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IAreaRepository _areas;
    private readonly IClock _clock;
    private readonly TaskEditService _edit;
    private readonly FocusService _focus;
    private readonly IReviewQueries _queries;
    private readonly InAppNotifier _notifier;
    private readonly IActivityLog _dziennik;
    private readonly NoteService _notes;
    private readonly ProjectEditService _projectEdit;

    public MainViewModel(
        InboxService inbox,
        ITaskRepository tasks,
        IProjectRepository projects,
        IAreaRepository areas,
        IClock clock,
        TaskEditService edit,
        FocusService focus,
        IReviewQueries queries,
        InAppNotifier notifier,
        ClarifyViewModel clarify,
        TaskDetailViewModel detail,
        ReviewViewModel review,
        NowViewModel nowVm,
        CalendarViewModel calendar,
        NotesViewModel notes,
        FiltersViewModel filters,
        SettingsViewModel settings,
        JournalViewModel journal,
        IActivityLog dziennik,
        NoteService noteService,
        ProjectEditService projectEdit)
    {
        _inbox = inbox;
        _projectEdit = projectEdit;
        _tasks = tasks;
        _projects = projects;
        _areas = areas;
        _clock = clock;
        _edit = edit;
        _focus = focus;
        _queries = queries;
        _notifier = notifier;
        _dziennik = dziennik;
        _notes = noteService;
        Clarify = clarify;
        Detail = detail;
        Review = review;
        Now = nowVm;
        Calendar = calendar;
        Notes = notes;
        Filters = filters;
        Settings = settings;
        Journal = journal;
        Clarify.Emptied += (_, _) => Bezpiecznie("Skrzynka opróżniona", ShowInboxAsync);

        // Po zapisie szczegółu ekran musi się przeliczyć: zmiana terminu albo dnia
        // wykonania potrafi przenieść zadanie na inną listę niż ta, z której je otwarto.
        Detail.Saved += (_, _) => Bezpiecznie("Ekran: odświeżenie po zapisie", ReloadAsync);

        // Przegląd zmienia stan zadań i projektów, więc ekran pod spodem musi się
        // przeliczyć — także liczniki niezmienników w „Dzisiaj".
        Review.Changed += (_, _) => Bezpiecznie("Ekran: odświeżenie po przeglądzie", ReloadAsync);

        // Krok skrzynki prowadzi do drzewka przetwarzania. Przegląd zostaje otwarty —
        // wznowi się na tym samym kroku, bo jego stan siedzi w bazie, a nie w ekranie.
        Review.InboxRequested += (_, _) => Bezpiecznie("Przegląd: skrzynka", ShowClarifyAsync);
        Now.Changed += (_, _) => Bezpiecznie("Teraz: odświeżenie piątki", RefreshFocusAsync);

        // Wgranie kopii zmienia wszystko naraz, więc ekran pod spodem musi się
        // przeliczyć — inaczej lista pokazuje stan sprzed wczytania, wyglądając
        // na aktualną.
        Settings.Imported += (_, _) => Bezpiecznie("Ekran: odświeżenie po wczytaniu kopii", ReloadAsync);

        // Kliknięcie w blok na siatce otwiera tę samą nakładkę, co kliknięcie na liście.
        Calendar.NewTaskRequested += (dzien, pora) =>
            Bezpiecznie("Kalendarz: nowe zadanie", () => Detail.NewAsync(dzien, pora));

        Calendar.TaskRequested += id => Bezpiecznie("Kalendarz: otwarcie zadania", async () =>
        {
            if (await _tasks.FindAsync(id) is { } zadanie)
            {
                await Detail.LoadAsync(zadanie);
            }
        });
    }

    public ClarifyViewModel Clarify { get; }

    public TaskDetailViewModel Detail { get; }

    public ReviewViewModel Review { get; }

    public NowViewModel Now { get; }

    public CalendarViewModel Calendar { get; }

    public NotesViewModel Notes { get; }

    public FiltersViewModel Filters { get; }

    public SettingsViewModel Settings { get; }

    public JournalViewModel Journal { get; }

    public ObservableCollection<TaskItem> InboxItems { get; } = [];

    public ObservableCollection<TaskRow> NextActions { get; } = [];

    public ObservableCollection<TaskRow> TodayItems { get; } = [];

    public ObservableCollection<TaskRow> PlanItems { get; } = [];

    /// <summary>Przypomnienia, które odezwały się przy tym uruchomieniu.</summary>
    public ObservableCollection<Notification> Reminders { get; } = [];

    public ObservableCollection<WaitingItem> WaitingItems { get; } = [];

    /// <summary>Tabela równowagi (8.5). Widoczna wyłącznie tutaj i w kroku 8 przeglądu.</summary>
    public ObservableCollection<AreaBalance> BalanceRows { get; } = [];

    /// <summary>Projekty bez następnej akcji (N1) — pozycja w „Dzisiaj".</summary>
    public ObservableCollection<BlockedProject> BlockedProjects { get; } = [];

    /// <summary>Oczekiwane, którym minął próg ponaglenia (N3) — pozycja w „Dzisiaj".</summary>
    public ObservableCollection<WaitingItem> Nudges { get; } = [];

    /// <summary>Pięć slotów wyboru na dziś (spec 8.6).</summary>
    public ObservableCollection<TaskItem> FocusItems { get; } = [];

    /// <summary>
    /// Piątka na dziś, której nie ma jeszcze na siatce.
    /// </summary>
    /// <remarks>
    /// Pasek nad kalendarzem ma przypominać o tym, czego **nie widać** poniżej.
    /// Zadanie z godziną ma już swój blok, więc powtórzone w pasku byłoby drugą kopią
    /// tej samej rzeczy — i to tą, która zabiera miejsce nad siatką.
    /// </remarks>
    public ObservableCollection<TaskItem> FocusOffGrid { get; } = [];

    /// <summary>Kandydaci do wyboru na dziś: „Następne" i zaplanowane na dziś lub wcześniej.</summary>
    public ObservableCollection<TaskRow> FocusCandidates { get; } = [];

    public ObservableCollection<TaskItem> SomedayItems { get; } = [];

    public ObservableCollection<TaskItem> ArchiveItems { get; } = [];

    public ObservableCollection<Area> AreaItems { get; } = [];

    public ObservableCollection<ProjectTreeRow> ProjectRows { get; } = [];

    /// <summary>
    /// Wyjaśnienie odmowy na ekranie „Projekty" — na przykład czemu projekt nie chce się usunąć.
    /// </summary>
    /// <remarks>
    /// Napis na ekranie, nie okienko: odmowa jest tu odpowiedzią na kliknięcie, które
    /// nie przyniosło skutku, a okienko wymaga jeszcze jednego kliknięcia, żeby wrócić
    /// do tego samego miejsca.
    /// </remarks>
    [ObservableProperty]
    public partial string ProjectsNotice { get; set; } = string.Empty;

    /// <summary>„Dzisiaj" jest ekranem startowym — to on odpowiada na pytanie „co teraz".</summary>
    [ObservableProperty]
    public partial Screen Current { get; set; } = Screen.Today;

    /// <summary>
    /// Czy okno jest wąskie — czyli czy to telefon albo wąskie okno na pulpicie.
    /// </summary>
    /// <remarks>
    /// Piętnaście przycisków nawigacji zawiniętych w pięć rzędów zjada na telefonie
    /// trzecią część ekranu, **zanim pojawi się cokolwiek treści**. Na pulpicie te same
    /// przyciski mieszczą się w jednym rzędzie i są najszybszą drogą do każdego ekranu.
    /// To nie jest jeden układ do poprawienia, tylko dwa różne układy do tej samej
    /// nawigacji: szeroko — wszystko naraz, wąsko — cztery pod kciukiem i reszta
    /// pod „Więcej".
    /// </remarks>
    [ObservableProperty]
    public partial bool IsNarrow { get; set; }

    /// <summary>Rozwinięta lista pozostałych ekranów. Tylko przy wąskim oknie.</summary>
    [ObservableProperty]
    public partial bool IsMoreOpen { get; set; }

    /// <summary>Nawigacja szeroka: wszystkie ekrany naraz.</summary>
    public bool ShowWideNav => !IsClarify && !IsNarrow;

    /// <summary>Pasek dolny: cztery pod kciukiem i „Więcej".</summary>
    public bool ShowBottomNav => !IsClarify && IsNarrow;

    /// <summary>
    /// Wrzut jest dostępny z każdego ekranu poza przetwarzaniem: myśl przychodzi wtedy,
    /// kiedy przychodzi, a nie wtedy, gdy akurat jesteś w skrzynce (spec 1.3).
    /// </summary>
    public bool ShowCapture => !IsClarify;

    [ObservableProperty]
    public partial int InboxCount { get; set; }

    /// <summary>Pole szybkiego wrzutu. Zasada 1.3: tylko tytuł, żadnych innych pól.</summary>
    [ObservableProperty]
    public partial string CaptureText { get; set; } = string.Empty;

    /// <summary>Avalonia nie zamienia liczby na wartość logiczną — potrzebne wprost.</summary>
    public bool HasInbox => InboxCount > 0;

    public bool HasReminders => Reminders.Count > 0;

    public bool IsToday => Current == Screen.Today;

    public bool IsNow => Current == Screen.Now;

    public bool FocusIsFull => FocusItems.Count >= FocusService.Slots;

    public string FocusCount => $"{FocusItems.Count} z {FocusService.Slots}";

    /// <summary>
    /// Zadanie czekające na zwolnienie slotu. Puste, dopóki piątka nie jest pełna.
    /// </summary>
    /// <remarks>
    /// Pytanie „które schodzi" bez pokazania czego dotyczy nie jest pytaniem, więc
    /// odmowa niesie ze sobą obecną piątkę i tę pozycję (spec 8.6).
    /// </remarks>
    [ObservableProperty]
    public partial TaskItem? PendingFocus { get; set; }

    public bool HasPendingFocus => PendingFocus is not null;

    partial void OnPendingFocusChanged(TaskItem? value) => OnPropertyChanged(nameof(HasPendingFocus));

    public bool IsInbox => Current == Screen.Inbox;

    public bool IsClarify => Current == Screen.Clarify;

    public bool IsNext => Current == Screen.Next;

    public bool IsPlans => Current == Screen.Plans;

    public bool IsProjects => Current == Screen.Projects;

    public bool IsSomeday => Current == Screen.Someday;

    public bool IsWaiting => Current == Screen.Waiting;

    public bool IsCalendar => Current == Screen.Calendar;

    public bool IsNotes => Current == Screen.Notes;

    public bool IsJournal => Current == Screen.Journal;

    public bool IsFilters => Current == Screen.Filters;

    public bool IsSettings => Current == Screen.Settings;

    public bool IsReview => Current == Screen.Review;

    public bool HasNudges => Nudges.Count > 0;

    /// <summary>
    /// Czy jest cokolwiek na dziś. Pusta lista ma powiedzieć, że jest pusto — nie
    /// zostawić prostokąta, po którym nie wiadomo, czy to brak zadań, czy brak wczytania.
    /// </summary>
    public bool HasToday => TodayItems.Count > 0;

    public bool HasBlocked => BlockedProjects.Count > 0;

    public bool IsAreas => Current == Screen.Areas;

    public bool IsArchive => Current == Screen.Archive;

    partial void OnInboxCountChanged(int value) => OnPropertyChanged(nameof(HasInbox));

    partial void OnCurrentChanged(Screen value)
    {
        OnPropertyChanged(nameof(IsToday));
        OnPropertyChanged(nameof(IsNow));
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(IsClarify));
        OnPropertyChanged(nameof(IsNext));
        OnPropertyChanged(nameof(IsPlans));
        OnPropertyChanged(nameof(IsProjects));
        OnPropertyChanged(nameof(IsSomeday));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsCalendar));
        OnPropertyChanged(nameof(IsNotes));
        OnPropertyChanged(nameof(IsJournal));
        OnPropertyChanged(nameof(IsFilters));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsAreas));
        OnPropertyChanged(nameof(IsArchive));

        // Wybranie czegokolwiek zamyka „Więcej". Lista, która zostaje otwarta nad
        // wybranym ekranem, wymaga drugiego gestu na zamknięcie i uczy, że nawigacja
        // to dwa kroki zamiast jednego.
        IsMoreOpen = false;

        OnPropertyChanged(nameof(ShowWideNav));
        OnPropertyChanged(nameof(ShowBottomNav));
        OnPropertyChanged(nameof(ShowCapture));
    }

    partial void OnIsNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWideNav));
        OnPropertyChanged(nameof(ShowBottomNav));
    }

    [RelayCommand]
    private void ToggleMore() => IsMoreOpen = !IsMoreOpen;

    [RelayCommand]
    private void CloseMore() => IsMoreOpen = false;

    public async Task InitializeAsync()
    {
        CollectReminders();
        await RefreshInboxAsync();

        // Kalendarz jako ekran startowy: pierwsze pytanie dnia brzmi „co dziś jest
        // umówione", a nie „co mam na liście".
        await ShowCalendarAsync();
    }

    /// <summary>
    /// Odbiera to, co uzbierała usługa przypomnień przy starcie.
    /// </summary>
    /// <remarks>
    /// Przypomnienia odpalają się w <c>PrepareAsync</c>, zanim okno ma cokolwiek
    /// wczytane. Zbieranie ich do odebrania, zamiast pokazywania od razu, jest tym,
    /// co pozwala im przetrwać tę chwilę.
    /// </remarks>
    private void CollectReminders()
    {
        foreach (var przypomnienie in _notifier.Drain())
        {
            Reminders.Add(przypomnienie);
        }

        OnPropertyChanged(nameof(HasReminders));
    }

    [RelayCommand]
    private void DismissReminders()
    {
        Reminders.Clear();
        OnPropertyChanged(nameof(HasReminders));
    }

    /// <summary>Przeładowuje bieżący ekran — po zapisie, który mógł zmienić przynależność.</summary>
    /// <summary>
    /// Robota wywołana zdarzeniem, z której wyjątek ma dokąd trafić.
    /// </summary>
    /// <remarks>
    /// Wszystkie te zdarzenia były dotąd obsługiwane wyrażeniami <c>async</c> bez
    /// wartości zwracanej. Taki kod nie ma komu oddać wyjątku: awaria odświeżania
    /// ekranu po zapisie kończyła się tym, że ekran po prostu **nie odświeżał się**,
    /// bez śladu w oknie i bez śladu nigdzie indziej. Wyglądało to dokładnie tak,
    /// jakby zapis się nie udał — a zapis się udawał.
    /// </remarks>
    private void Bezpiecznie(string co, Func<Task> praca) => _ = Probuj(co, praca);

    private async Task Probuj(string co, Func<Task> praca)
    {
        try
        {
            await praca();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _dziennik.RecordAsync(
                co, "nie udało się", ActivityLevel.Problem, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Przeliczenie ekranu po zmianie danych.
    /// </summary>
    /// <remarks>
    /// Skrzynka i bieżący ekran osobno, każde z własnym zabezpieczeniem. Do dziś szły
    /// jednym ciągiem: wywrotka przy liczeniu skrzynki zabierała ze sobą odświeżenie
    /// ekranu, na którym akurat się było, i nic się nie przerysowywało — po zapisie,
    /// który się udał. Jedno popsute miejsce ma psuć jedno miejsce.
    /// </remarks>
    private async Task ReloadAsync()
    {
        await Probuj("Ekran: przeliczenie skrzynki", RefreshInboxAsync);

        var zadanie = Current switch
        {
            Screen.Today => ShowTodayAsync(),
            Screen.Now => Now.LoadAsync(),
            Screen.Next => ShowNextAsync(),
            Screen.Plans => ShowPlansAsync(),
            Screen.Someday => ShowSomedayAsync(),
            Screen.Archive => ShowArchiveAsync(),
            Screen.Projects => ShowProjectsAsync(),
            Screen.Waiting => ShowWaitingAsync(),
            Screen.Calendar => Calendar.LoadAsync(),
            Screen.Notes => Notes.LoadAsync(),
            Screen.Journal => Journal.LoadAsync(),
            Screen.Filters => Filters.RunCommand.ExecuteAsync(null),
            Screen.Areas => ShowAreasAsync(),
            _ => Task.CompletedTask,
        };

        await Probuj($"Ekran: przeliczenie ({Current})", () => zadanie);
    }

    /// <summary>
    /// Piątka na dziś i kandydaci do niej. Kandydaci to „Następne" oraz zaplanowane
    /// na dziś albo wcześniej (spec 8.6) — nie wszystko, co ma dzisiejszą datę.
    /// </summary>
    private async Task RefreshFocusAsync()
    {
        var dzis = Today();

        FocusItems.Clear();
        FocusOffGrid.Clear();

        foreach (var zadanie in await _focus.TodayAsync())
        {
            FocusItems.Add(zadanie);

            // Bez godziny nie ma bloku na siatce, więc pasek nad kalendarzem jest
            // jedynym miejscem, gdzie to zadanie w ogóle widać.
            if (zadanie.DoTime is null && zadanie.State != TaskState.Done)
            {
                FocusOffGrid.Add(zadanie);
            }
        }

        var wybrane = FocusItems.Select(t => t.Id).ToHashSet();
        var kandydaci = (await _tasks.ByStateAsync(TaskState.Next))
            .Concat(await _tasks.ByStateAsync(TaskState.Scheduled))
            .Where(t => t.State == TaskState.Next || t.DoDate <= dzis)
            .Where(t => !wybrane.Contains(t.Id));

        FocusCandidates.Clear();
        foreach (var zadanie in kandydaci)
        {
            FocusCandidates.Add(TaskRow.From(zadanie, dzis));
        }

        OnPropertyChanged(nameof(FocusIsFull));
        OnPropertyChanged(nameof(FocusCount));
    }

    /// <summary>Wybór zadania na dziś. Przy pełnej piątce pyta, które schodzi.</summary>
    [RelayCommand]
    private async Task FocusAsync(TaskRow? row)
    {
        if (row is null)
        {
            return;
        }

        var wynik = await _focus.TryFocusAsync(row.Task.Id);

        if (!wynik.Accepted)
        {
            PendingFocus = row.Task;
            await RefreshFocusAsync();
            return;
        }

        PendingFocus = null;
        await RefreshFocusAsync();
    }

    /// <summary>
    /// Zdjęcie z wyboru. Jeśli coś czekało na slot, wchodzi na zwolnione miejsce.
    /// </summary>
    [RelayCommand]
    private async Task UnfocusAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        await _focus.UnfocusAsync(task.Id);

        if (PendingFocus is { } czekajace)
        {
            await _focus.TryFocusAsync(czekajace.Id);
            PendingFocus = null;
        }

        await RefreshFocusAsync();
    }

    [RelayCommand]
    private void CancelPendingFocus() => PendingFocus = null;

    /// <summary>Otwarcie szczegółu — jedyne wejście do terminu, przypomnienia i rytmu.</summary>
    [RelayCommand]
    private async Task OpenAsync(TaskRow? row)
    {
        if (row is not null)
        {
            await Detail.LoadAsync(row.Task);
        }
    }

    /// <summary>
    /// Wrzut. Dostępny z każdego ekranu poza przetwarzaniem — myśl przychodzi wtedy,
    /// kiedy przychodzi, a nie wtedy, gdy akurat jesteś w skrzynce.
    /// </summary>
    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (string.IsNullOrWhiteSpace(CaptureText))
        {
            return;
        }

        await _inbox.CaptureAsync(CaptureText);
        CaptureText = string.Empty;
        await RefreshInboxAsync();
    }

    [RelayCommand]
    private Task ShowInboxAsync()
    {
        Current = Screen.Inbox;
        return RefreshInboxAsync();
    }

    [RelayCommand]
    private async Task ShowClarifyAsync()
    {
        if (InboxCount == 0)
        {
            return;
        }

        Current = Screen.Clarify;
        await Clarify.LoadAsync();
    }

    [RelayCommand]
    private async Task ShowNextAsync()
    {
        Current = Screen.Next;
        await Fill(NextActions, _tasks.ByStateAsync(TaskState.Next));
    }

    [RelayCommand]
    private async Task ShowProjectsAsync()
    {
        Current = Screen.Projects;
        ProjectRows.Clear();

        var zablokowane = (await _queries.BlockedProjectsAsync())
            .Select(p => p.ProjectId)
            .ToHashSet();

        var rows = ProjectTree.Build(
            await _areas.ActiveAsync(), await _projects.ActiveAsync(), zablokowane);

        foreach (var row in rows)
        {
            ProjectRows.Add(new ProjectTreeRow(row));
        }
    }

    [RelayCommand]
    private async Task ShowWaitingAsync()
    {
        Current = Screen.Waiting;
        WaitingItems.Clear();

        foreach (var pozycja in await _queries.WaitingAsync(Today()))
        {
            WaitingItems.Add(pozycja);
        }
    }

    /// <summary>Notatki — materiał referencyjny, którego nie trzeba robić.</summary>
    [RelayCommand]
    private async Task ShowNotesAsync()
    {
        Current = Screen.Notes;
        await Notes.LoadAsync();
    }

    /// <summary>Ustawienia: motyw, strefa, kopia zapasowa (spec 11, 12).</summary>
    [RelayCommand]
    private void ShowSettings()
    {
        Settings.Load();
        Current = Screen.Settings;
    }

    /// <summary>
    /// Co się działo (spec 12).
    /// </summary>
    /// <remarks>
    /// Osobny ekran, a nie kawałek Ustawień: zagląda się tu wtedy, gdy coś nie wyszło,
    /// i wtedy nie chce się przewijać pola na sekrety Google, żeby dojść do odpowiedzi.
    /// </remarks>
    [RelayCommand]
    private async Task ShowJournalAsync()
    {
        Current = Screen.Journal;
        await Journal.LoadAsync();
    }

    /// <summary>Własne widoki — konstruktor warunków i Ulubione (spec 11.5).</summary>
    [RelayCommand]
    private async Task ShowFiltersAsync()
    {
        Current = Screen.Filters;
        await Filters.LoadAsync();
    }

    /// <summary>Kalendarz godzinowy — wydarzenia i zadania na jednej siatce.</summary>
    [RelayCommand]
    private async Task ShowCalendarAsync()
    {
        Current = Screen.Calendar;
        await Calendar.LoadAsync();
    }

    /// <summary>Kreator przeglądu. Wznawia niedokończony albo zakłada nowy.</summary>
    [RelayCommand]
    private async Task ShowReviewAsync()
    {
        Current = Screen.Review;
        await Review.OpenAsync();
    }

    /// <summary>Widok „Teraz" — trzy do pięciu pozycji po wyborze czasu i energii.</summary>
    [RelayCommand]
    private async Task ShowNowAsync()
    {
        Current = Screen.Now;
        await Now.LoadAsync();
    }

    [RelayCommand]
    private async Task ShowTodayAsync()
    {
        Current = Screen.Today;
        var dzis = Today();
        await Fill(TodayItems, _tasks.TodayAsync(dzis));
        OnPropertyChanged(nameof(HasToday));
        await RefreshFocusAsync();

        // Ponaglenia (N3) i projekty zablokowane (N1) idą na „Dzisiaj", bo są sprawami
        // na dziś. Cisza obszarów (N10) **nigdy tu nie trafia** — to nie jest sprawa na
        // dziś, a codzienne przypominanie o niej zamieniłoby ją w szum (spec 6).
        Nudges.Clear();
        foreach (var pozycja in (await _queries.WaitingAsync(dzis)).Where(w => w.NeedsNudge))
        {
            Nudges.Add(pozycja);
        }

        BlockedProjects.Clear();
        foreach (var projekt in await _queries.BlockedProjectsAsync())
        {
            BlockedProjects.Add(projekt);
        }

        OnPropertyChanged(nameof(HasNudges));
        OnPropertyChanged(nameof(HasBlocked));
    }

    [RelayCommand]
    private async Task ShowPlansAsync()
    {
        Current = Screen.Plans;
        var dzis = Today();
        await Fill(PlanItems, _tasks.UpcomingAsync(dzis, dzis.AddDays(30)));
    }

    [RelayCommand]
    private async Task ShowSomedayAsync()
    {
        Current = Screen.Someday;
        await FillPlain(SomedayItems, _tasks.ByStateAsync(TaskState.Someday));
    }

    [RelayCommand]
    private async Task ShowArchiveAsync()
    {
        Current = Screen.Archive;
        await FillPlain(ArchiveItems, _tasks.ArchiveAsync(limit: 200));
    }

    [RelayCommand]
    private async Task ShowAreasAsync()
    {
        Current = Screen.Areas;
        AreaItems.Clear();
        foreach (var area in await _areas.AllAsync())
        {
            AreaItems.Add(area);
        }

        BalanceRows.Clear();
        foreach (var wiersz in await _queries.BalanceAsync(Today()))
        {
            BalanceRows.Add(wiersz);
        }
    }

    private DateOnly Today() => _clock.Today;

    /// <summary>Dzisiaj w strefie z ustawień — dla menu, które samo zegara nie ma.</summary>
    public DateOnly Dzisiaj => _clock.Today;

    private async Task Fill(ObservableCollection<TaskRow> target, Task<IReadOnlyList<TaskItem>> source)
    {
        var items = await source;
        var dzis = Today();

        target.Clear();
        foreach (var item in items)
        {
            target.Add(TaskRow.From(item, dzis));
        }
    }

    private static async Task FillPlain(
        ObservableCollection<TaskItem> target, Task<IReadOnlyList<TaskItem>> source)
    {
        var items = await source;
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>
    /// Czynności menu podręcznego. Każda bierze samo zadanie, bo wiersze list różnią się
    /// typem, a menu ma być jedno dla wszystkich.
    /// </summary>
    [RelayCommand]
    public async Task OpenTaskAsync(TaskItem? task)
    {
        if (task is not null)
        {
            await Detail.LoadAsync(task);
        }
    }

    [RelayCommand]
    public async Task FocusTaskAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        var wynik = await _focus.TryFocusAsync(task.Id);

        PendingFocus = wynik.Accepted ? null : task;
        await RefreshFocusAsync();
    }

    /// <summary>Przełożenie o dobę. Godzina zostaje — przesunięcie dnia jej nie dotyczy.</summary>
    [RelayCommand]
    private async Task PostponeTaskAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        var skad = task.DoDate ?? Today();

        await _edit.RescheduleAsync(task.Id, skad.AddDays(1), task.DoTime);
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task TrashTaskAsync(TaskItem? task)
    {
        if (task is not null)
        {
            await _inbox.TrashAsync(task.Id);
            await ReloadAsync();
        }
    }

    /// <summary>
    /// Czynności menu podręcznego, wołane wprost z okna.
    /// </summary>
    /// <remarks>
    /// Metody, nie polecenia: menu składa się w kodzie okna, a każda pozycja niesie
    /// własny argument — rodzaj rytmu, wagę, projekt. Polecenie przyjmuje jeden
    /// parametr, więc byłoby ich tyle, ile pozycji, i każde z własną obsługą pustki.
    /// </remarks>
    public async Task SetDateAsync(TaskItem task, DateOnly? day)
    {
        if (day is { } dzien)
        {
            await _edit.RescheduleAsync(task.Id, dzien, task.DoTime);
        }
        else
        {
            await _edit.ApplyAsync(task.Id, Bez(task));
        }

        await ReloadAsync();
    }

    /// <summary>Ten sam zestaw pól, tylko bez dnia wykonania — reszta ma zostać.</summary>
    private static TaskEdit Bez(TaskItem task) =>
        new(task.Title, task.Note, null, task.Deadline, task.ReminderAt, task.Recurrence,
            task.Priority, task.EstimatedMinutes, task.Energy, task.AreaId, null);

    public async Task SetPriorityAsync(TaskItem task, Priority priority)
    {
        await _edit.SetPriorityAsync(task.Id, priority);
        await ReloadAsync();
    }

    public async Task SetRecurrenceAsync(TaskItem task, RecurrenceKind? kind)
    {
        await _edit.SetRecurrenceAsync(task.Id, kind);
        await ReloadAsync();
    }

    public async Task SetProjectAsync(TaskItem task, Guid? projectId)
    {
        await _edit.SetProjectAsync(task.Id, projectId);
        await ReloadAsync();
    }

    /// <summary>Barwa wiersza z ekranu „Projekty" — obszaru albo projektu.</summary>
    public async Task SetRowColorAsync(ProjectTreeRow row, string? color)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsArea)
        {
            await _projectEdit.SetAreaColorAsync(row.Id, color);
        }
        else
        {
            await _projectEdit.SetProjectColorAsync(row.Id, color);
        }

        ProjectsNotice = string.Empty;
        await ShowProjectsAsync();
    }

    /// <summary>
    /// Usunięcie projektu. Obszarów stąd nie da się usunąć — zostałyby po nich
    /// zadania bez przynależności, a każde zadanie musi mieć obszar (N11).
    /// </summary>
    public async Task DeleteProjectAsync(ProjectTreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsArea)
        {
            ProjectsNotice = "Obszaru nie usuwa się stąd — można go wyłączyć na ekranie „Obszary”.";
            return;
        }

        ProjectsNotice = await _projectEdit.DeleteProjectAsync(row.Id) ?? string.Empty;
        await ShowProjectsAsync();
    }

    /// <summary>Czy ten projekt da się usunąć — menu ma nie proponować rzeczy bez skutku.</summary>
    public async Task<string?> WhyCannotDeleteAsync(ProjectTreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.IsArea
            ? "Obszaru nie usuwa się stąd."
            : await _projectEdit.WhyCannotDeleteAsync(row.Id);
    }

    public async Task ToNoteAsync(TaskItem task)
    {
        await _notes.ConvertToNoteAsync(task.Id);
        await ReloadAsync();
    }

    /// <summary>Pokazanie zadania na siatce — kalendarz przeskakuje na jego dzień.</summary>
    public async Task ShowInCalendarAsync(TaskItem task)
    {
        if (task.DoDate is { } dzien)
        {
            Calendar.Anchor = dzien;
        }

        await ShowCalendarAsync();
    }

    /// <summary>Projekty do wyboru w menu. Same czynne — zamkniętego nie ma po co proponować.</summary>
    public async Task<IReadOnlyList<Project>> ActiveProjectsAsync() =>
        (await _projects.AllAsync())
            .Where(p => !p.Deleted && p.State == ProjectState.Active)
            .OrderBy(p => p.Outcome, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Zadanie po identyfikatorze — dla bloków siatki, które niosą sam identyfikator.</summary>
    public Task<TaskItem?> FindTaskAsync(Guid id) => _tasks.FindAsync(id);

    /// <summary>Odhaczenie zadania podanego wprost — piątka „Na dziś" niesie same zadania.</summary>
    [RelayCommand]
    public async Task CompleteTaskAsync(TaskItem? task)
    {
        if (task is not null)
        {
            await _edit.CompleteAsync(task.Id);
            await ReloadAsync();
        }
    }

    /// <summary>
    /// Odhaczenie z listy. Idzie przez szczegół, bo zadanie powtarzalne musi przy
    /// okazji zrodzić kolejne wystąpienie (8.4) — a z listy tego nie widać.
    /// </summary>
    [RelayCommand]
    private async Task CompleteAsync(TaskRow? row)
    {
        if (row is null)
        {
            return;
        }

        await _edit.CompleteAsync(row.Task.Id);
        await ReloadAsync();
    }

    private async Task RefreshInboxAsync()
    {
        InboxItems.Clear();
        foreach (var item in await _inbox.ListAsync())
        {
            InboxItems.Add(item);
        }

        InboxCount = InboxItems.Count;
    }
}
