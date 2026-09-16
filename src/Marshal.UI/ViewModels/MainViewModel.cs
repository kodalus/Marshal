using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

public enum Screen
{
    Inbox,
    Clarify,
    Next,
    Projects,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly InboxService _inbox;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;

    public MainViewModel(
        InboxService inbox,
        ITaskRepository tasks,
        IProjectRepository projects,
        ClarifyViewModel clarify)
    {
        _inbox = inbox;
        _tasks = tasks;
        _projects = projects;
        Clarify = clarify;
        Clarify.Emptied += async (_, _) => await ShowInboxAsync();
    }

    public ClarifyViewModel Clarify { get; }

    public ObservableCollection<TaskItem> InboxItems { get; } = [];

    public ObservableCollection<TaskItem> NextActions { get; } = [];

    public ObservableCollection<Project> Projects { get; } = [];

    [ObservableProperty]
    public partial Screen Current { get; set; } = Screen.Inbox;

    [ObservableProperty]
    public partial int InboxCount { get; set; }

    /// <summary>Pole szybkiego wrzutu. Zasada 1.3: tylko tytuł, żadnych innych pól.</summary>
    [ObservableProperty]
    public partial string CaptureText { get; set; } = string.Empty;

    /// <summary>Avalonia nie zamienia liczby na wartość logiczną — potrzebne wprost.</summary>
    public bool HasInbox => InboxCount > 0;

    public bool IsInbox => Current == Screen.Inbox;

    public bool IsClarify => Current == Screen.Clarify;

    public bool IsNext => Current == Screen.Next;

    public bool IsProjects => Current == Screen.Projects;

    partial void OnInboxCountChanged(int value) => OnPropertyChanged(nameof(HasInbox));

    partial void OnCurrentChanged(Screen value)
    {
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(IsClarify));
        OnPropertyChanged(nameof(IsNext));
        OnPropertyChanged(nameof(IsProjects));
    }

    public Task InitializeAsync() => ShowInboxAsync();

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
        Projects.Clear();
        foreach (var project in await _projects.ActiveAsync())
        {
            Projects.Add(project);
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
