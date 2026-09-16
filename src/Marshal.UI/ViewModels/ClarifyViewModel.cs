using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Ekran przetwarzania skrzynki — drzewko z rozdziału 7 spec.
/// </summary>
/// <remarks>
/// Pokazuje **jedną pozycję naraz, bez listy w tle**. Lista pozostałych pozycji jest
/// rozpraszaczem i zachętą do przeskakiwania między nimi zamiast rozstrzygania
/// tej, która jest na wierzchu.
/// </remarks>
public sealed partial class ClarifyViewModel(
    InboxService inbox,
    IAreaRepository areas) : ObservableObject
{
    [ObservableProperty]
    public partial TaskItem? Current { get; set; }

    [ObservableProperty]
    public partial int Remaining { get; set; }

    [ObservableProperty]
    public partial AreaChoice? SelectedArea { get; set; }

    [ObservableProperty]
    public partial string WaitingForWho { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? ScheduledFor { get; set; }

    [ObservableProperty]
    public partial string ProjectOutcome { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public ObservableCollection<AreaChoice> Areas { get; } = [];

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    /// <summary>Zgłaszane, gdy skrzynka opustoszeje — ekran nie ma czego pokazywać.</summary>
    public event EventHandler? Emptied;

    public async Task LoadAsync()
    {
        if (Areas.Count == 0)
        {
            foreach (var area in await areas.ActiveAsync())
            {
                Areas.Add(AreaChoice.From(area));
            }
        }

        await NextAsync();
    }

    private async Task NextAsync()
    {
        var pending = await inbox.ListAsync();
        Remaining = pending.Count;
        Current = pending.FirstOrDefault();
        Problem = null;
        WaitingForWho = string.Empty;
        ProjectOutcome = string.Empty;
        ScheduledFor = null;

        if (Current is null)
        {
            Emptied?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task Trash() => Run(id => inbox.TrashAsync(id), needsArea: false);

    [RelayCommand]
    private Task Someday() => Run(id => inbox.PostponeAsync(id, SelectedArea!.Id, null));

    [RelayCommand]
    private Task DoNow() => Run(id => inbox.DoNowAsync(id, SelectedArea!.Id));

    [RelayCommand]
    private Task Delegate() =>
        Run(id => inbox.DelegateAsync(id, SelectedArea!.Id, WaitingForWho),
            validate: () => string.IsNullOrWhiteSpace(WaitingForWho) ? "Na kogo czekasz?" : null);

    [RelayCommand]
    private Task MakeNext() => Run(id => inbox.MakeNextAsync(id, SelectedArea!.Id));

    [RelayCommand]
    private Task Schedule() =>
        Run(id => inbox.ScheduleAsync(id, SelectedArea!.Id, DateOnly.FromDateTime(ScheduledFor!.Value.Date)),
            validate: () => ScheduledFor is null ? "Na który dzień?" : null);

    [RelayCommand]
    private Task PromoteToProject() =>
        Run(id => inbox.PromoteToProjectAsync(id, SelectedArea!.Id, ProjectOutcome, Current!.Title),
            validate: () => string.IsNullOrWhiteSpace(ProjectOutcome)
                ? "Po czym poznasz, że projekt jest skończony?"
                : null);

    private async Task Run(Func<Guid, Task> action, bool needsArea = true, Func<string?>? validate = null)
    {
        if (Current is null)
        {
            return;
        }

        if (needsArea && SelectedArea is null)
        {
            Problem = "Wybierz obszar.";
            return;
        }

        if (validate?.Invoke() is { } problem)
        {
            Problem = problem;
            return;
        }

        await action(Current.Id);
        await NextAsync();
    }
}
