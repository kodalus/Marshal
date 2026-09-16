using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Filters;
using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Konstruktor warunków i Ulubione (spec 11, 11.5).
/// </summary>
/// <remarks>
/// <para>
/// Wszystkie osie widoczne naraz, każda domyślnie wyłączona — zamiast przycisku
/// „dodaj warunek", który prowadzi do listy pól, a z niej do edytora zależnego od
/// wybranego pola. Tamta droga jest ogólniejsza i kosztuje trzy kliknięcia oraz
/// pamiętanie, czego się jeszcze nie ustawiło. Tutaj widać w jednym spojrzeniu,
/// **czym filtr jest** — łącznie z tym, co w nim nieustawione.
/// </para>
/// <para>
/// Wyniki przeliczane po każdej zmianie, bez przycisku „szukaj". Filtr układa się
/// metodą prób: włączam stan, widzę czterdzieści pozycji, dokładam obszar, widzę
/// osiem. Przycisk zamieniłby to w serię osobnych decyzji „czy warto sprawdzić".
/// </para>
/// </remarks>
public sealed partial class FiltersViewModel : ObservableObject
{
    private readonly FilterService _filters;
    private readonly IAreaRepository _areas;
    private readonly IProjectRepository _projects;
    private readonly ITagRepository _tags;
    private readonly IClock _clock;

    /// <summary>Wstrzymuje przeliczanie na czas wypełniania pól z zapisanego widoku.</summary>
    private bool _wczytywanie;

    public FiltersViewModel(
        FilterService filters,
        IAreaRepository areas,
        IProjectRepository projects,
        ITagRepository tags,
        IClock clock)
    {
        _filters = filters;
        _areas = areas;
        _projects = projects;
        _tags = tags;
        _clock = clock;

        Fill(States, TaskStates);
        Fill(Priorities, PriorityChoice.All.Select(p => (p.Value.ToString(), p.Label)));
        Fill(Energies, EnergyLevelChoice.All.Select(e => (e.Value.ToString(), e.Label)));
    }

    /// <summary>
    /// Stany na przełącznikach. Kosz nieobecny: filtr po wyrzuconych jest pytaniem
    /// „co skasowałam", a na nie odpowiada Archiwum.
    /// </summary>
    private static IEnumerable<(string, string)> TaskStates =>
    [
        (nameof(TaskState.Inbox), "w skrzynce"),
        (nameof(TaskState.Next), "następne"),
        (nameof(TaskState.Scheduled), "zaplanowane"),
        (nameof(TaskState.Waiting), "oczekiwane"),
        (nameof(TaskState.Someday), "kiedyś"),
        (nameof(TaskState.Done), "zrobione"),
    ];

    public ObservableCollection<SavedFilter> Favourites { get; } = [];

    public ObservableCollection<FilterToggle> States { get; } = [];

    public ObservableCollection<FilterToggle> Priorities { get; } = [];

    public ObservableCollection<FilterToggle> Energies { get; } = [];

    public ObservableCollection<FilterToggle> Tags { get; } = [];

    public ObservableCollection<ScopeChoice> Areas { get; } = [];

    public ObservableCollection<ScopeChoice> Projects { get; } = [];

    public ObservableCollection<TaskRow> Results { get; } = [];

    public IReadOnlyList<WindowChoice> Windows => WindowChoice.All;

    public IReadOnlyList<MinutesChoice> MinuteOptions => MinutesChoice.All;

    [ObservableProperty]
    public partial ScopeChoice? Area { get; set; }

    [ObservableProperty]
    public partial ScopeChoice? Project { get; set; }

    [ObservableProperty]
    public partial WindowChoice? Deadline { get; set; }

    [ObservableProperty]
    public partial WindowChoice? DoDate { get; set; }

    [ObservableProperty]
    public partial MinutesChoice? Minutes { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Identyfikator otwartego Ulubionego albo pusty, gdy filtr jest doraźny.</summary>
    [ObservableProperty]
    public partial Guid OpenId { get; set; }

    public bool HasResults => Results.Count > 0;

    public bool HasFavourites => Favourites.Count > 0;

    public bool IsSaved => OpenId != Guid.Empty;

    /// <summary>Widok bez żadnego warunku nie ma czego zapisać ani czego pokazać.</summary>
    public bool CanSave => !Build().IsEmpty && !string.IsNullOrWhiteSpace(Name);

    public string Empty => Build().IsEmpty
        ? "Włącz choć jeden warunek. Filtr bez warunków pokazałby całą bazę, a to nie jest odpowiedź na żadne pytanie."
        : "Nic nie pasuje.";

    public async Task LoadAsync()
    {
        _wczytywanie = true;

        Areas.Clear();
        Areas.Add(ScopeChoice.Any);
        foreach (var obszar in await _areas.AllAsync())
        {
            Areas.Add(new ScopeChoice(obszar.Id, obszar.Name));
        }

        Projects.Clear();
        Projects.Add(ScopeChoice.Any);
        Projects.Add(ScopeChoice.None);
        foreach (var projekt in await _projects.AllAsync())
        {
            Projects.Add(new ScopeChoice(projekt.Id, projekt.Name));
        }

        Tags.Clear();
        foreach (var tag in await _tags.AllAsync())
        {
            Dodaj(Tags, tag.Id.ToString(), tag.Name);
        }

        Area ??= ScopeChoice.Any;
        Project ??= ScopeChoice.Any;
        Deadline ??= WindowChoice.All[0];
        DoDate ??= WindowChoice.All[0];
        Minutes ??= MinutesChoice.All[0];

        await ReloadFavouritesAsync();

        _wczytywanie = false;
        await RunAsync();
    }

    /// <summary>Warunki złożone z tego, co w tej chwili włączone.</summary>
    public FilterQuery Build()
    {
        var warunki = new List<FilterCondition>();

        Dolacz<TaskState>(warunki, States, FilterCondition.States);
        Dolacz<Priority>(warunki, Priorities, FilterCondition.Priorities);
        Dolacz<Energy>(warunki, Energies, FilterCondition.Energies);

        if (Wlaczone(Tags) is { Length: > 0 } tagi)
        {
            warunki.Add(FilterCondition.Tags([.. tagi.Select(Guid.Parse)]));
        }

        if (Area?.Id is { } obszar)
        {
            warunki.Add(FilterCondition.Areas(obszar));
        }

        if (Project?.Id is { } projekt)
        {
            warunki.Add(FilterCondition.Projects(projekt));
        }

        if (Deadline?.Value is { } termin)
        {
            warunki.Add(FilterCondition.Deadline(termin));
        }

        if (DoDate?.Value is { } dzien)
        {
            warunki.Add(FilterCondition.DoDate(dzien));
        }

        if (Minutes?.Value is { } minuty)
        {
            warunki.Add(FilterCondition.Estimate(minuty));
        }

        if (!string.IsNullOrWhiteSpace(Text))
        {
            warunki.Add(FilterCondition.Contains(Text));
        }

        return new FilterQuery(warunki);
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (_wczytywanie)
        {
            return;
        }

        var dzis = DateOnly.FromDateTime(_clock.Now.LocalDateTime);

        Results.Clear();
        foreach (var zadanie in await _filters.RunAsync(Build()))
        {
            Results.Add(TaskRow.From(zadanie, dzis));
        }

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(Empty));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>Wczytanie zapisanego widoku do konstruktora.</summary>
    [RelayCommand]
    private async Task OpenAsync(SavedFilter? filter)
    {
        if (filter?.Query is not { } zapytanie)
        {
            return;
        }

        _wczytywanie = true;

        Wyczysc();
        OpenId = filter.Id;
        Name = filter.Name;

        foreach (var warunek in zapytanie.Conditions)
        {
            Zastosuj(warunek);
        }

        _wczytywanie = false;

        OnPropertyChanged(nameof(IsSaved));
        await RunAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var zapytanie = Build();

        if (zapytanie.IsEmpty || string.IsNullOrWhiteSpace(Name))
        {
            return;
        }

        if (OpenId == Guid.Empty)
        {
            OpenId = (await _filters.SaveAsync(Name, zapytanie)).Id;
        }
        else
        {
            await _filters.UpdateAsync(OpenId, Name, zapytanie);
        }

        OnPropertyChanged(nameof(IsSaved));
        await ReloadFavouritesAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (OpenId == Guid.Empty)
        {
            return;
        }

        await _filters.DeleteAsync(OpenId);
        await ResetAsync();
        await ReloadFavouritesAsync();
    }

    /// <summary>Wyczyszczenie konstruktora. Zapisany widok zostaje w Ulubionych.</summary>
    [RelayCommand]
    private async Task ResetAsync()
    {
        _wczytywanie = true;
        Wyczysc();
        _wczytywanie = false;

        OnPropertyChanged(nameof(IsSaved));
        await RunAsync();
    }

    private async Task ReloadFavouritesAsync()
    {
        Favourites.Clear();
        foreach (var filtr in await _filters.FavouritesAsync())
        {
            Favourites.Add(filtr);
        }

        OnPropertyChanged(nameof(HasFavourites));
    }

    private void Wyczysc()
    {
        OpenId = Guid.Empty;
        Name = string.Empty;
        Text = string.Empty;
        Area = ScopeChoice.Any;
        Project = ScopeChoice.Any;
        Deadline = WindowChoice.All[0];
        DoDate = WindowChoice.All[0];
        Minutes = MinutesChoice.All[0];

        foreach (var przelacznik in States.Concat(Priorities).Concat(Energies).Concat(Tags))
        {
            przelacznik.IsOn = false;
        }
    }

    private void Zastosuj(FilterCondition warunek)
    {
        switch (warunek.Field)
        {
            case FilterField.State:
                Wlacz(States, warunek.Values);
                break;
            case FilterField.Priority:
                Wlacz(Priorities, warunek.Values);
                break;
            case FilterField.Energy:
                Wlacz(Energies, warunek.Values);
                break;
            case FilterField.Tag:
                Wlacz(Tags, warunek.Values);
                break;
            case FilterField.Area:
                Area = Znajdz(Areas, warunek.Values);
                break;
            case FilterField.Project:
                Project = Znajdz(Projects, warunek.Values);
                break;
            case FilterField.Deadline:
                Deadline = WindowChoice.All.FirstOrDefault(w => w.Value == warunek.Window);
                break;
            case FilterField.DoDate:
                DoDate = WindowChoice.All.FirstOrDefault(w => w.Value == warunek.Window);
                break;
            case FilterField.Estimate:
                // Widok zapisany na urządzeniu z inną listą minut ma się otworzyć,
                // a nie zniknąć: brakująca wartość dokładana do listy na miejscu.
                Minutes = MinutesChoice.All.FirstOrDefault(m => m.Value == warunek.MaxMinutes)
                    ?? new MinutesChoice(warunek.MaxMinutes, $"{warunek.MaxMinutes} min");
                break;
            case FilterField.Text:
                Text = warunek.Text ?? string.Empty;
                break;
        }
    }

    private static void Wlacz(
        IEnumerable<FilterToggle> przelaczniki, IReadOnlyList<string> wartosci)
    {
        foreach (var przelacznik in przelaczniki)
        {
            przelacznik.IsOn = wartosci.Contains(przelacznik.Value);
        }
    }

    /// <summary>
    /// Wartość, której już nie ma — skasowany obszar, projekt z drugiego urządzenia —
    /// wraca jako „dowolny", a nie jako pusta pozycja bez nazwy.
    /// </summary>
    private static ScopeChoice? Znajdz(
        IEnumerable<ScopeChoice> pozycje, IReadOnlyList<string> wartosci) =>
        pozycje.FirstOrDefault(p => p.Id is { } id && wartosci.Contains(id.ToString()))
        ?? ScopeChoice.Any;

    private static string[] Wlaczone(IEnumerable<FilterToggle> przelaczniki) =>
        przelaczniki.Where(p => p.IsOn).Select(p => p.Value).ToArray();

    private static void Dolacz<T>(
        List<FilterCondition> warunki,
        IEnumerable<FilterToggle> przelaczniki,
        Func<T[], FilterCondition> zbuduj)
        where T : struct, Enum
    {
        if (Wlaczone(przelaczniki) is { Length: > 0 } wartosci)
        {
            warunki.Add(zbuduj([.. wartosci.Select(Enum.Parse<T>)]));
        }
    }

    private void Fill(
        ObservableCollection<FilterToggle> gdzie, IEnumerable<(string Value, string Label)> co)
    {
        foreach (var (wartosc, etykieta) in co)
        {
            Dodaj(gdzie, wartosc, etykieta);
        }
    }

    /// <summary>
    /// Przełącznik podpięty do przeliczania. Zdarzenie, nie powiązanie po stronie okna:
    /// inaczej każdy nowy ekran z tymi samymi przełącznikami musiałby pamiętać o tym,
    /// żeby je podpiąć — a zapomnienie nie daje żadnego objawu poza listą, która
    /// milczy.
    /// </summary>
    private void Dodaj(ObservableCollection<FilterToggle> gdzie, string wartosc, string etykieta)
    {
        var przelacznik = new FilterToggle(etykieta, wartosc);
        przelacznik.PropertyChanged += async (_, _) => await RunAsync();
        gdzie.Add(przelacznik);
    }

    partial void OnAreaChanged(ScopeChoice? value) => _ = RunAsync();

    partial void OnProjectChanged(ScopeChoice? value) => _ = RunAsync();

    partial void OnDeadlineChanged(WindowChoice? value) => _ = RunAsync();

    partial void OnDoDateChanged(WindowChoice? value) => _ = RunAsync();

    partial void OnMinutesChanged(MinutesChoice? value) => _ = RunAsync();

    partial void OnTextChanged(string value) => _ = RunAsync();

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));
}
