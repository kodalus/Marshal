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
    NoteService notes,
    IAreaRepository areas,
    TaskEditService edit) : ObservableObject
{
    [ObservableProperty]
    public partial TaskItem? Current { get; set; }

    /// <summary>
    /// Ile to zajmie i ile trzeba mieć w sobie — pytane tu, przy decyzji.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ekran „Teraz" dobiera zadania pod dostępne minuty i poziom sił (spec 8.1),
    /// więc zadanie bez oszacowania nie trafia tam **nigdy**. Do dziś jedyne miejsce,
    /// gdzie dało się to wpisać, było w szczegółach — czyli trzeba było najpierw
    /// przetworzyć skrzynkę, potem znaleźć zadanie na liście i otworzyć je jeszcze raz.
    /// Dwa kroki na coś, co jest częścią tej samej decyzji.
    /// </para>
    /// <para>
    /// Pytane tutaj, bo tutaj myśl jest jeszcze świeża: „zadzwonić do przychodni"
    /// oszacuje się lepiej w chwili, gdy się o tym myśli, niż tydzień później z listy.
    /// Puste zostaje puste — zgadywanie długości byłoby gorsze od jej braku.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial decimal? EstimatedMinutes { get; set; }

    [ObservableProperty]
    public partial EnergyLevelChoice? SelectedEnergy { get; set; } = EnergyLevelChoice.All[0];

    public IReadOnlyList<EnergyLevelChoice> Energies => EnergyLevelChoice.All;

    /// <summary>Podpowiedź pod polami: czemu to w ogóle pytanie.</summary>
    public static string EstimateHint =>
        "Bez tego zadanie nie trafi do „Teraz” — ten ekran dobiera pod dostępne minuty "
        + "i poziom sił. Puste zostaje puste; zgadywanie jest gorsze od braku.";

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

        // Długość i siły należą do **tego** wrzutu — zostawione, przykleiłyby się
        // do następnego, a ten bywa zupełnie inną robotą.
        EstimatedMinutes = null;
        SelectedEnergy = Energies[0];

        if (Current is null)
        {
            Emptied?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task Trash() => Run(id => inbox.TrashAsync(id), needsArea: false);

    [RelayCommand]
    private Task Someday() => Run(id => inbox.PostponeAsync(id, SelectedArea!.Id, null));

    /// <summary>
    /// Gałąź „materiał referencyjny" z drzewka (spec 7): nie wymaga działania, ale ma
    /// być pod ręką.
    /// </summary>
    /// <remarks>
    /// Bez obszaru, bo notatka bywa ogólna — „numer do przychodni" nie należy do
    /// żadnego obszaru odpowiedzialności bardziej niż do innego, a wymuszony wybór
    /// byłby zmyśleniem.
    /// </remarks>
    [RelayCommand]
    private Task ToNote() => Run(id => notes.ConvertToNoteAsync(id), needsArea: false);

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

    /// <summary>Dopisanie długości i sił do zadania, które właśnie wyszło ze skrzynki.</summary>
    private async Task ZapiszOszacowanieAsync(Guid id)
    {
        var minuty = EstimatedMinutes is { } liczba ? (int)liczba : (int?)null;
        var sila = SelectedEnergy?.Value ?? Energy.Unknown;

        if (minuty is null && sila == Energy.Unknown)
        {
            return;
        }

        await edit.SetEstimateAsync(id, minuty, sila);
    }

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

        var identyfikator = Current.Id;

        await action(identyfikator);

        // Oszacowanie po przejściu stanu, nie przed: gałęzie kosza i notatki nie mają
        // czego szacować, a zadanie przeniesione do projektu ma już własny byt.
        await ZapiszOszacowanieAsync(identyfikator);

        await NextAsync();
    }
}
