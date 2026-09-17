using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application;
using Marshal.Application.UseCases;
using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

/// <summary>Dostępny czas jako przycisk, nie pole do wpisania.</summary>
/// <remarks>
/// Cztery gotowe odpowiedzi zamiast liczby: pytanie „ile masz minut" zadane w polu
/// tekstowym każe liczyć, a wtedy zamiast zacząć — planujesz.
/// </remarks>
public sealed record MinutesChoice(int Minutes, string Label)
{
    public static readonly IReadOnlyList<MinutesChoice> All =
    [
        new(15, "15 minut"),
        new(30, "pół godziny"),
        new(60, "godzina"),
        new(120, "dwie godziny"),
    ];

    public override string ToString() => Label;
}

public sealed record EnergyChoice(Energy Value, string Label)
{
    public static readonly IReadOnlyList<EnergyChoice> All =
    [
        new(Energy.Low, "resztki"),
        new(Energy.Medium, "średnio"),
        new(Energy.High, "pełna"),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// Widok „Teraz" (spec 8.1): aplikacja wybiera za Ciebie.
/// </summary>
/// <remarks>
/// Nie da się go rozwinąć do pełnej listy (11.1). Pełna lista jest w „Projektach",
/// a ten ekran ma odpowiadać na jedno pytanie — co teraz — i tylko na nie.
/// </remarks>
public sealed partial class NowViewModel(NowService now, TaskEditService edit) : ObservableObject
{
    /// <summary>Czas i energia przełącza się obok siebie, a kontekst bazy jest jeden.</summary>
    private readonly LatestOnly _kolejka = new();

    [ObservableProperty]
    public partial MinutesChoice SelectedMinutes { get; set; } = MinutesChoice.All[1];

    [ObservableProperty]
    public partial EnergyChoice SelectedEnergy { get; set; } = EnergyChoice.All[1];

    [ObservableProperty]
    public partial int Unestimated { get; set; }

    /// <summary>
    /// Czy sięgnąć także do „kiedyś-może".
    /// </summary>
    /// <remarks>
    /// Wyłączone na starcie i przy każdym wejściu, bo „kiedyś-może" jest z założenia
    /// poza systemem rzeczy do zrobienia. Zapamiętane zostawiałoby ten ekran na stałe
    /// otwarty na wszystko, co się kiedykolwiek odłożyło — czyli zamieniłoby go
    /// w drugą listę wszystkiego.
    /// </remarks>
    [ObservableProperty]
    public partial bool AlsoSomeday { get; set; }

    public ObservableCollection<NowPick> Picks { get; } = [];

    public IReadOnlyList<MinutesChoice> Minutes => MinutesChoice.All;

    public IReadOnlyList<EnergyChoice> Energies => EnergyChoice.All;

    public bool HasPicks => Picks.Count > 0;

    /// <summary>
    /// Gdy kandydatów jest mniej niż trzy, ekran proponuje oszacowanie zamiast
    /// pokazywać pustkę (spec 8.1).
    /// </summary>
    /// <remarks>
    /// Mikrozadanie na minutę, które samo się rozwiązuje w miarę używania. Pusty ekran
    /// z komunikatem „nic nie pasuje" byłby prawdą bezużyteczną.
    /// </remarks>
    public bool SuggestEstimating => Picks.Count < NowService.TooFewCandidates && Unestimated > 0;

    public string EstimatePrompt =>
        $"Masz {Unestimated} zadań bez oszacowania. Oszacuj kilka — to minuta, a ekran zacznie mieć z czego wybierać.";

    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        // Podpowiedź z pory dnia ustawiana tylko przy wejściu, nigdy w trakcie:
        // przestawienie suwaka użytkownikowi pod ręką jest gorsze od złej podpowiedzi.
        SelectedEnergy = Energies.First(e => e.Value == now.SuggestEnergy());

        // Zerowane przy każdym wejściu, nie zapamiętywane: sięgnięcie do „kiedyś-może"
        // ma być decyzją podjętą teraz, a nie stanem, w którym ekran został z zeszłego
        // tygodnia i po cichu pokazuje wszystko.
        AlsoSomeday = false;

        await RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => _kolejka.RunAsync(OdswiezAsync);

    private async Task OdswiezAsync()
    {
        Unestimated = await now.UnestimatedCountAsync();

        Picks.Clear();
        foreach (var pick in await now.PickAsync(
            SelectedMinutes.Minutes, SelectedEnergy.Value, AlsoSomeday))
        {
            Picks.Add(pick);
        }

        OnPropertyChanged(nameof(HasPicks));
        OnPropertyChanged(nameof(SuggestEstimating));
        OnPropertyChanged(nameof(EstimatePrompt));
    }

    [RelayCommand]
    private async Task CompleteAsync(NowPick? pick)
    {
        if (pick is null)
        {
            return;
        }

        await edit.CompleteAsync(pick.Task.Id);
        await RefreshAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Przeliczenie po zmianie suwaka. Wywołanie bez czekania, bo powiadomienie
    // o zmianie właściwości nie jest asynchroniczne — to jedyne miejsce w oknie,
    // gdzie wyjątek z odczytu bazy nie miałby gdzie wypłynąć. Odczyt jest prosty
    // i bezstanowy, więc koszt tego ustępstwa jest znany i ograniczony.
    partial void OnSelectedMinutesChanged(MinutesChoice value) => _ = RefreshAsync();

    partial void OnSelectedEnergyChanged(EnergyChoice value) => _ = RefreshAsync();

    partial void OnAlsoSomedayChanged(bool value) => _ = RefreshAsync();
}
