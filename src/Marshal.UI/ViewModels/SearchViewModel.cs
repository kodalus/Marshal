using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Notes;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Szukanie po całej aplikacji — po nazwach i treściach zadań oraz notatek.
/// </summary>
/// <remarks>
/// <para>
/// Jedno pole i dwie listy pod nim. Osobne szukanie w zadaniach i osobne w notatkach
/// kazałoby wiedzieć przed szukaniem, gdzie się coś zapisało — a to jest dokładnie ta
/// wiedza, której się nie ma, kiedy się szuka.
/// </para>
/// <para>
/// Szuka się przy każdym znaku, a nie po naciśnięciu przycisku: lista zwężająca się
/// w trakcie pisania mówi od razu, czy słowo jest to, czy nie. Kontekst bazy jest
/// jeden, więc przebiegi idą przez <see cref="LatestOnly"/> — liczy się wynik
/// ostatniego pytania, a nie wszystkie po kolei.
/// </para>
/// </remarks>
public sealed partial class SearchViewModel(
    SearchService search, IClock clock) : ObservableObject
{
    private readonly LatestOnly _queue = new();

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    public ObservableCollection<TaskRow> Tasks { get; } = [];

    public ObservableCollection<NoteCard> Notes { get; } = [];

    /// <summary>Czy w ogóle o cokolwiek zapytano.</summary>
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    public bool HasTasks => Tasks.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    /// <summary>
    /// Pustka nazwana wprost. Lista bez wierszy i lista, o którą nikt nie zapytał,
    /// wyglądają identycznie — a znaczą co innego.
    /// </summary>
    public string Message => !HasQuery
        ? "Wpisz słowo, które pamiętasz. Szukanie idzie po nazwach i po treści — "
            + "zadań i notatek naraz, razem ze zrobionymi i wyrzuconymi."
        : HasTasks || HasNotes
            ? string.Empty
            : $"Nic nie pasuje do „{Query.Trim()}”.";

    public bool HasMessage => Message.Length > 0;

    /// <summary>Wczytanie ekranu: te same wyniki, co przed wyjściem z niego.</summary>
    public Task LoadAsync() => RunAsync();

    [RelayCommand]
    private Task RunAsync() => _queue.RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        var hits = await search.FindAsync(Query);
        var today = clock.Today;

        Tasks.Clear();
        foreach (var task in hits.Tasks)
        {
            Tasks.Add(TaskRow.From(task, today));
        }

        Notes.Clear();
        foreach (var note in hits.Notes)
        {
            Notes.Add(new NoteCard(note, note.Title, NotesViewModel.Beginning(note.Content)));
        }

        Announce();
    }

    /// <summary>Wyczyszczenie pola — jednym ruchem, bo kasowanie słowa po znaku to nie ruch.</summary>
    [RelayCommand]
    private void Clear()
    {
        Query = string.Empty;
    }

    /// <summary>Zgłaszane po wybraniu notatki — otwiera ją ekran notatek.</summary>
    public event Action<Note>? NoteRequested;

    [RelayCommand]
    private void OpenNote(NoteCard? card)
    {
        if (card is not null)
        {
            NoteRequested?.Invoke(card.Note);
        }
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
    }

    partial void OnQueryChanged(string value) => _ = RunAsync();
}
