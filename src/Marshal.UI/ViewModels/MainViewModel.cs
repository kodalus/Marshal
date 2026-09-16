using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Domain.Tasks;

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
    Areas,
    Archive,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly InboxService _inbox;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IAreaRepository _areas;
    private readonly IClock _clock;

    public MainViewModel(
        InboxService inbox,
        ITaskRepository tasks,
        IProjectRepository projects,
        IAreaRepository areas,
        IClock clock,
        ClarifyViewModel clarify)
    {
        _inbox = inbox;
        _tasks = tasks;
        _projects = projects;
        _areas = areas;
        _clock = clock;
        Clarify = clarify;
        Clarify.Emptied += async (_, _) => await ShowInboxAsync();
    }

    public ClarifyViewModel Clarify { get; }

    public ObservableCollection<TaskItem> InboxItems { get; } = [];

    public ObservableCollection<TaskItem> NextActions { get; } = [];

    public ObservableCollection<TaskItem> TodayItems { get; } = [];

    public ObservableCollection<TaskItem> PlanItems { get; } = [];

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

    public bool IsToday => Current == Screen.Today;

    public bool IsInbox => Current == Screen.Inbox;

    public bool IsClarify => Current == Screen.Clarify;

    public bool IsNext => Current == Screen.Next;

    public bool IsPlans => Current == Screen.Plans;

    public bool IsProjects => Current == Screen.Projects;

    public bool IsSomeday => Current == Screen.Someday;

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
        OnPropertyChanged(nameof(IsAreas));
        OnPropertyChanged(nameof(IsArchive));
    }

    public async Task InitializeAsync()
    {
        await RefreshInboxAsync();
        await ShowTodayAsync();
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
        NextActions.Clear();
        foreach (var task in await _tasks.ByStateAsync(TaskState.Next))
        {
            NextActions.Add(task);
        }
    }

    [RelayCommand]
    private async Task ShowProjectsAsync()
    {
        Current = Screen.Projects;
        ProjectRows.Clear();

        var rows = ProjectTree.Build(await _areas.ActiveAsync(), await _projects.ActiveAsync());
        foreach (var row in rows)
        {
            ProjectRows.Add(row);
        }
    }

    [RelayCommand]
    private async Task ShowTodayAsync()
    {
        Current = Screen.Today;
        await Fill(TodayItems, _tasks.TodayAsync(Today()));
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
        await Fill(SomedayItems, _tasks.ByStateAsync(TaskState.Someday));
    }

    [RelayCommand]
    private async Task ShowArchiveAsync()
    {
        Current = Screen.Archive;
        await Fill(ArchiveItems, _tasks.ArchiveAsync(limit: 200));
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
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.Now.Date);

    private static async Task Fill(ObservableCollection<TaskItem> target, Task<IReadOnlyList<TaskItem>> source)
    {
        var items = await source;
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    [RelayCommand]
    private async Task CompleteAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        await _inbox.DoNowAsync(task.Id, task.AreaId!.Value);
        await ShowNextAsync();
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
