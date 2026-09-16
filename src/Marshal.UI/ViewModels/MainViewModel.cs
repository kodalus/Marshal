using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.Review;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Notifications;

namespace Marshal.UI.ViewModels;

public enum Screen
{
    Today,
    Inbox,
    Clarify,
    Next,
    Plans,
    Projects,
    Someday,
    Waiting,
    Areas,
    Archive,
    Review,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly InboxService _inbox;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IAreaRepository _areas;
    private readonly IClock _clock;
    private readonly TaskEditService _edit;
    private readonly IReviewQueries _queries;
    private readonly InAppNotifier _notifier;

    public MainViewModel(
        InboxService inbox,
        ITaskRepository tasks,
        IProjectRepository projects,
        IAreaRepository areas,
        IClock clock,
        TaskEditService edit,
        IReviewQueries queries,
        InAppNotifier notifier,
        ClarifyViewModel clarify,
        TaskDetailViewModel detail,
        ReviewViewModel review)
    {
        _inbox = inbox;
        _tasks = tasks;
        _projects = projects;
        _areas = areas;
        _clock = clock;
        _edit = edit;
        _queries = queries;
        _notifier = notifier;
        Clarify = clarify;
        Detail = detail;
        Review = review;
        Clarify.Emptied += async (_, _) => await ShowInboxAsync();

        // Po zapisie szczegółu ekran musi się przeliczyć: zmiana terminu albo dnia
        // wykonania potrafi przenieść zadanie na inną listę niż ta, z której je otwarto.
        Detail.Saved += async (_, _) => await ReloadAsync();

        // Przegląd zmienia stan zadań i projektów, więc ekran pod spodem musi się
        // przeliczyć — także liczniki niezmienników w „Dzisiaj".
        Review.Changed += async (_, _) => await ReloadAsync();
    }

    public ClarifyViewModel Clarify { get; }

    public TaskDetailViewModel Detail { get; }

    public ReviewViewModel Review { get; }

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

    public ObservableCollection<TaskItem> SomedayItems { get; } = [];

    public ObservableCollection<TaskItem> ArchiveItems { get; } = [];

    public ObservableCollection<Area> AreaItems { get; } = [];

    public ObservableCollection<ProjectRow> ProjectRows { get; } = [];

    /// <summary>„Dzisiaj" jest ekranem startowym — to on odpowiada na pytanie „co teraz".</summary>
    [ObservableProperty]
    public partial Screen Current { get; set; } = Screen.Today;

    [ObservableProperty]
    public partial int InboxCount { get; set; }

    /// <summary>Pole szybkiego wrzutu. Zasada 1.3: tylko tytuł, żadnych innych pól.</summary>
    [ObservableProperty]
    public partial string CaptureText { get; set; } = string.Empty;

    /// <summary>Avalonia nie zamienia liczby na wartość logiczną — potrzebne wprost.</summary>
    public bool HasInbox => InboxCount > 0;

    public bool HasReminders => Reminders.Count > 0;

    public bool IsToday => Current == Screen.Today;

    public bool IsInbox => Current == Screen.Inbox;

    public bool IsClarify => Current == Screen.Clarify;

    public bool IsNext => Current == Screen.Next;

    public bool IsPlans => Current == Screen.Plans;

    public bool IsProjects => Current == Screen.Projects;

    public bool IsSomeday => Current == Screen.Someday;

    public bool IsWaiting => Current == Screen.Waiting;

    public bool IsReview => Current == Screen.Review;

    public bool HasNudges => Nudges.Count > 0;

    public bool HasBlocked => BlockedProjects.Count > 0;

    public bool IsAreas => Current == Screen.Areas;

    public bool IsArchive => Current == Screen.Archive;

    partial void OnInboxCountChanged(int value) => OnPropertyChanged(nameof(HasInbox));

    partial void OnCurrentChanged(Screen value)
    {
        OnPropertyChanged(nameof(IsToday));
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(IsClarify));
        OnPropertyChanged(nameof(IsNext));
        OnPropertyChanged(nameof(IsPlans));
        OnPropertyChanged(nameof(IsProjects));
        OnPropertyChanged(nameof(IsSomeday));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsAreas));
        OnPropertyChanged(nameof(IsArchive));
    }

    public async Task InitializeAsync()
    {
        CollectReminders();
        await RefreshInboxAsync();
        await ShowTodayAsync();
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
    private async Task ReloadAsync()
    {
        await RefreshInboxAsync();

        var zadanie = Current switch
        {
            Screen.Today => ShowTodayAsync(),
            Screen.Next => ShowNextAsync(),
            Screen.Plans => ShowPlansAsync(),
            Screen.Someday => ShowSomedayAsync(),
            Screen.Archive => ShowArchiveAsync(),
            Screen.Projects => ShowProjectsAsync(),
            Screen.Waiting => ShowWaitingAsync(),
            Screen.Areas => ShowAreasAsync(),
            _ => Task.CompletedTask,
        };

        await zadanie;
    }

    /// <summary>Otwarcie szczegółu — jedyne wejście do terminu, przypomnienia i rytmu.</summary>
    [RelayCommand]
    private void Open(TaskRow? row)
    {
        if (row is not null)
        {
            Detail.Load(row.Task);
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
            ProjectRows.Add(row);
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

    /// <summary>Kreator przeglądu. Wznawia niedokończony albo zakłada nowy.</summary>
    [RelayCommand]
    private async Task ShowReviewAsync()
    {
        Current = Screen.Review;
        await Review.OpenAsync();
    }

    [RelayCommand]
    private async Task ShowTodayAsync()
    {
        Current = Screen.Today;
        var dzis = Today();
        await Fill(TodayItems, _tasks.TodayAsync(dzis));

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

    private DateOnly Today() => DateOnly.FromDateTime(_clock.Now.Date);

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
