using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Review;
using Marshal.Application.UseCases;
using Marshal.Domain.Notes;
using Marshal.Domain.Review;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Kreator przeglądu tygodniowego (spec 8.3).
/// </summary>
/// <remarks>
/// Osiem kroków, każdy z licznikiem. Stan zapisywany po **każdej pojedynczej pozycji**,
/// nie po kroku — przerwanie po czterech minutach ma zostawić przegląd w 30% ukończony
/// i wznawialny dokładnie w tym miejscu. Brak ekranu „zacznij od nowa" jest połową
/// wartości tego mechanizmu.
/// </remarks>
public sealed partial class ReviewViewModel(
    ReviewService review,
    IReviewQueries queries,
    NoteService notes,
    IClock clock) : ObservableObject
{
    private static readonly (ReviewStep Step, string Title, string Hint)[] Steps =
    [
        (ReviewStep.Pinned, "Zasady", "Do przeczytania. Nic do zrobienia — reszta przeglądu ma się dziać po tym, a nie zamiast tego."),
        (ReviewStep.Inbox, "Opróżnij skrzynkę", "Każda pozycja dostaje rozstrzygnięcie."),
        (ReviewStep.Overdue, "Zaległe i przeterminowane", "Termin minął. Świat się nie przesunął — trzeba coś z tym zrobić."),
        (ReviewStep.Nudges, "Oczekiwane — ponaglić?", "Czeka dłużej niż próg tego obszaru."),
        (ReviewStep.Blocked, "Projekty zablokowane", "Projekt bez następnej akcji stoi, choć wygląda na żywy."),
        (ReviewStep.StaleProjects, "Projekty nietknięte", "Dwa tygodnie bez ruchu. Nadal aktualne?"),
        (ReviewStep.Someday, "Kiedyś-może — coś dojrzało?", "Minęła data, na którą było odłożone."),
        (ReviewStep.Upcoming, "Dwa tygodnie w przód", "Co nadchodzi i czy jest na to miejsce."),
        (ReviewStep.Counters, "Liczniki, które o coś pytają", "Nie ocena, tylko sygnał, że zapis jest nieprawdziwy. Zwykle odpowiedź brzmi: to nie jedno zadanie, tylko projekt."),
        (ReviewStep.Balance, "Równowaga obszarów", "Do obejrzenia. Bez ocen i bez wyrównywania — równowaga nie znaczy równy rozkład."),
    ];

    private ReviewSession? _session;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial int StepIndex { get; set; }

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    /// <summary>Ile pozycji zostało w całym przeglądzie, nie tylko w tym kroku.</summary>
    [ObservableProperty]
    public partial int TotalRemaining { get; set; }

    public ObservableCollection<ReviewItem> Items { get; } = [];

    public ObservableCollection<AreaBalance> Balance { get; } = [];

    /// <summary>Przypięte notatki — wizja i zasady, krok zerowy (spec 8.3, 1.8).</summary>
    public ObservableCollection<Note> Pinned { get; } = [];

    public bool HasPinned => Pinned.Count > 0;

    public string StepTitle => Steps[StepIndex].Title;

    public string StepHint => Steps[StepIndex].Hint;

    public string StepNumber => $"Krok {StepIndex} z {Steps.Length - 1}";

    public bool HasItems => Items.Count > 0;

    public bool IsBalanceStep => Steps[StepIndex].Step == ReviewStep.Balance;

    public bool IsPinnedStep => Steps[StepIndex].Step == ReviewStep.Pinned;

    /// <summary>
    /// Krok skrzynki działa inaczej: pozycji nie odhacza się, tylko się je przetwarza.
    /// </summary>
    /// <remarks>
    /// Oznaczenie wrzutu jako „rozpatrzony" bez podjęcia decyzji zostawiłoby go
    /// w skrzynce — a za tydzień trzeba by go oznaczyć znowu. Bieżnia zamiast
    /// opróżniania. Ten krok prowadzi więc do drzewka przetwarzania (rozdz. 7),
    /// a licznik schodzi sam, w miarę jak skrzynka pustoszeje.
    /// </remarks>
    public bool IsInboxStep => Steps[StepIndex].Step == ReviewStep.Inbox;

    /// <summary>Kroki, w których nie ma czego odhaczać: tekst i tabela.</summary>
    public bool HasChecklist => !IsPinnedStep && !IsBalanceStep && !IsInboxStep;

    public bool CanGoBack => StepIndex > 0;

    public bool IsLastStep => StepIndex == Steps.Length - 1;

    /// <summary>
    /// Ile pozycji zostało w tym kroku. Zero **nie jest** powodem do pochwały ani do
    /// czerwieni — to po prostu krok bez pracy.
    /// </summary>
    public string Remaining => Items.Count switch
    {
        0 when IsInboxStep => "skrzynka jest pusta",
        0 => "nic do rozpatrzenia",
        var n when IsInboxStep => $"{n} w skrzynce",
        var n => $"{n} do rozpatrzenia",
    };

    public event EventHandler? Changed;

    /// <summary>Krok skrzynki prosi o przejście do drzewka przetwarzania.</summary>
    public event EventHandler? InboxRequested;

    [RelayCommand]
    private void ProcessInbox() => InboxRequested?.Invoke(this, EventArgs.Empty);

    public async Task OpenAsync()
    {
        _session = await review.StartOrResumeAsync();
        StepIndex = Math.Clamp(_session.CurrentStep, 0, Steps.Length - 1);
        IsFinished = false;
        IsOpen = true;
        await LoadStepAsync();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private async Task NextAsync()
    {
        if (IsLastStep)
        {
            await FinishAsync();
            return;
        }

        await GoAsync(StepIndex + 1);
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        if (CanGoBack)
        {
            await GoAsync(StepIndex - 1);
        }
    }

    /// <summary>
    /// Odhaczenie pozycji. Zapis od razu — to jest cały mechanizm wznawialności,
    /// a nie optymalizacja.
    /// </summary>
    [RelayCommand]
    private async Task ProcessAsync(ReviewItem? item)
    {
        if (item is null || _session is null)
        {
            return;
        }

        await review.MarkProcessedAsync(_session, item.Id);
        Items.Remove(item);
        RefreshCounters();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task FinishAsync()
    {
        if (_session is not null)
        {
            await review.CompleteAsync(_session);
        }

        IsFinished = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task GoAsync(int index)
    {
        StepIndex = index;

        if (_session is not null)
        {
            await review.GoToAsync(_session, index);
        }

        await LoadStepAsync();
    }

    private async Task LoadStepAsync()
    {
        if (_session is null)
        {
            return;
        }

        Items.Clear();
        foreach (var item in await review.ItemsAsync(Steps[StepIndex].Step, _session))
        {
            Items.Add(item);
        }

        Pinned.Clear();
        if (IsPinnedStep)
        {
            foreach (var notatka in await notes.PinnedAsync())
            {
                Pinned.Add(notatka);
            }
        }

        OnPropertyChanged(nameof(HasPinned));

        Balance.Clear();
        if (IsBalanceStep)
        {
            foreach (var wiersz in await queries.BalanceAsync(
                DateOnly.FromDateTime(clock.Now.DateTime)))
            {
                Balance.Add(wiersz);
            }
        }

        TotalRemaining = (await review.CountsAsync()).Total;
        RefreshCounters();
    }

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepHint));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(IsBalanceStep));
        OnPropertyChanged(nameof(IsPinnedStep));
        OnPropertyChanged(nameof(IsInboxStep));
        OnPropertyChanged(nameof(HasChecklist));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStep));
    }

    private void RefreshCounters()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Remaining));
    }
}
