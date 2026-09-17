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
    IProjectRepository projects,
    FocusService focus,
    TaskEditService edit) : ObservableObject
{
    /// <summary>Komunikat do pokazania po przejściu do następnego wrzutu.</summary>
    private string? _doPowiedzenia;

    /// <summary>
    /// Który wrzut z kolejki jest na wierzchu.
    /// </summary>
    /// <remarks>
    /// Ekran pokazywał zawsze pierwszy i nie dawało się go pominąć bez rozstrzygnięcia.
    /// To wygląda na dyscyplinę, ale nią nie jest: wrzut, którego akurat nie da się
    /// rozstrzygnąć — bo trzeba do kogoś zadzwonić albo czegoś sprawdzić — blokował
    /// całą resztę i kończyło się zamknięciem ekranu. Przewijanie nie psuje zasady
    /// „jedna pozycja naraz": nadal widać jedną, tylko da się wybrać którą.
    /// </remarks>
    private int _numer;

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

    /// <summary>Który z ilu — żeby przewijanie kolejki nie było ruchem w ciemno.</summary>
    public string Position => Remaining == 0 ? string.Empty : $"{_numer + 1} z {Remaining}";

    public bool CanMove => Remaining > 1;

    /// <summary>
    /// Miejsce: obszar albo projekt w nim — to samo drzewko, co w szczegółach zadania.
    /// </summary>
    /// <remarks>
    /// Sama lista obszarów znaczyła, że wrzut przetworzony do istniejącego projektu
    /// trzeba było potem znaleźć na liście i przypiąć ręcznie. „To projekt" zakłada
    /// nowy, a nie wkłada do istniejącego — i to są dwie różne rzeczy, z których
    /// dostępna była tylko pierwsza.
    /// </remarks>
    [ObservableProperty]
    public partial PlacementChoice? SelectedArea { get; set; }

    [ObservableProperty]
    public partial string WaitingForWho { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? ScheduledFor { get; set; }

    [ObservableProperty]
    public partial string ProjectOutcome { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public ObservableCollection<PlacementChoice> Areas { get; } = [];

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    /// <summary>Zgłaszane, gdy skrzynka opustoszeje — ekran nie ma czego pokazywać.</summary>
    public event EventHandler? Emptied;

    public async Task LoadAsync()
    {
        // Dociągane przy każdym wejściu, nie raz na życie okna: obszary i projekty
        // zmienia się na osobnych ekranach, a zapamiętana lista robi się nieprawdziwa
        // dokładnie wtedy, gdy ktoś właśnie założył projekt i chce do niego coś wrzucić.
        var drzewko = ProjectTree.Build(await areas.ActiveAsync(), await projects.ActiveAsync());

        Areas.Clear();
        foreach (var wiersz in drzewko)
        {
            Areas.Add(PlacementChoice.From(wiersz));
        }

        await NextAsync();
    }

    /// <summary>Następny wrzut w kolejce — bez rozstrzygania tego, co na wierzchu.</summary>
    [RelayCommand]
    private Task Skip() => PrzesunAsync(1);

    /// <summary>Poprzedni wrzut.</summary>
    [RelayCommand]
    private Task Back() => PrzesunAsync(-1);

    private async Task PrzesunAsync(int o)
    {
        var pending = await inbox.ListAsync();

        if (pending.Count == 0)
        {
            return;
        }

        _numer = ((_numer + o) % pending.Count + pending.Count) % pending.Count;
        await NextAsync(zachowajNumer: true);
    }

    private async Task NextAsync(bool zachowajNumer = false)
    {
        var pending = await inbox.ListAsync();
        Remaining = pending.Count;

        if (!zachowajNumer)
        {
            // Po rozstrzygnięciu zostajemy w tym samym miejscu kolejki: następny wrzut
            // wchodzi pod ten sam numer. Skok na początek kazałby przewijać od nowa
            // do miejsca, w którym się było.
            _numer = pending.Count == 0 ? 0 : Math.Min(_numer, pending.Count - 1);
        }

        Current = pending.Count == 0 ? null : pending[_numer];
        Problem = null;
        WaitingForWho = string.Empty;
        ProjectOutcome = string.Empty;
        ScheduledFor = null;

        // Długość i siły należą do **tego** wrzutu — zostawione, przykleiłyby się
        // do następnego, a ten bywa zupełnie inną robotą.
        EstimatedMinutes = null;
        SelectedEnergy = Energies[0];

        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(CanMove));

        if (Current is null)
        {
            Emptied?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task Trash() => Run(id => inbox.TrashAsync(id), needsArea: false);

    [RelayCommand]
    private Task Someday() => Run(id => inbox.PostponeAsync(id, SelectedArea!.AreaId, null));

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
    private Task DoNow() => Run(id => inbox.DoNowAsync(id, SelectedArea!.AreaId));

    [RelayCommand]
    private Task Delegate() =>
        Run(id => inbox.DelegateAsync(id, SelectedArea!.AreaId, WaitingForWho),
            validate: () => string.IsNullOrWhiteSpace(WaitingForWho) ? "Na kogo czekasz?" : null);

    [RelayCommand]
    private Task MakeNext() =>
        Run(id => inbox.MakeNextAsync(id, SelectedArea!.AreaId, SelectedArea.ProjectId));

    /// <summary>
    /// Następna akcja **i od razu na dziś**.
    /// </summary>
    /// <remarks>
    /// Z przetwarzania nie dawało się dotąd wziąć czegoś na dziś w ogóle: „Zaplanuj"
    /// nadaje dzień, czyli stawia zadanie na siatce kalendarza, a to jest inna decyzja
    /// niż „robię to dzisiaj, nie wiem o której". Przy wrzucie, który się właśnie
    /// oszacowało, ta druga jest częstsza — i była jedyną, której tu brakowało.
    /// </remarks>
    [RelayCommand]
    private Task Today() =>
        Run(async id =>
        {
            await inbox.MakeNextAsync(id, SelectedArea!.AreaId, SelectedArea.ProjectId);

            if (!(await focus.TryFocusAsync(id)).Accepted)
            {
                // Odkładane, nie ustawiane wprost: przejście do następnego wrzutu
                // czyści komunikat, więc napisany tutaj zniknąłby w tej samej chwili.
                _doPowiedzenia = "Pięć zadań na dziś już jest — to zostaje wśród następnych akcji.";
            }
        });

    [RelayCommand]
    private Task Schedule() =>
        Run(id => inbox.ScheduleAsync(id, SelectedArea!.AreaId, DateOnly.FromDateTime(ScheduledFor!.Value.Date)),
            validate: () => ScheduledFor is null ? "Na który dzień?" : null);

    [RelayCommand]
    private Task PromoteToProject() =>
        Run(id => inbox.PromoteToProjectAsync(id, SelectedArea!.AreaId, ProjectOutcome, Current!.Title),
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

        Problem = null;

        var identyfikator = Current.Id;

        await action(identyfikator);

        // Oszacowanie po przejściu stanu, nie przed: gałęzie kosza i notatki nie mają
        // czego szacować, a zadanie przeniesione do projektu ma już własny byt.
        await ZapiszOszacowanieAsync(identyfikator);

        await NextAsync();

        if (_doPowiedzenia is { } slowo)
        {
            Problem = slowo;
            _doPowiedzenia = null;
        }
    }
}
