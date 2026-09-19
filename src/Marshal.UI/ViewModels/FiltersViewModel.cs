using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application;
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
    private bool _loading;

    private readonly LatestOnly _queue = new();

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

    public IReadOnlyList<EstimateChoice> MinuteOptions => EstimateChoice.All;

    [ObservableProperty]
    public partial ScopeChoice? Area { get; set; }

    [ObservableProperty]
    public partial ScopeChoice? Project { get; set; }

    [ObservableProperty]
    public partial WindowChoice? Deadline { get; set; }

    [ObservableProperty]
    public partial WindowChoice? DoDate { get; set; }

    [ObservableProperty]
    public partial EstimateChoice? Minutes { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Identyfikator otwartego Ulubionego albo pusty, gdy filtr jest doraźny.</summary>
    [ObservableProperty]
    public partial Guid OpenId { get; set; }

    /// <summary>Jedno zdanie, gdy jest co powiedzieć. Pusty przez większość czasu.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    public bool HasStatus => Status.Length > 0;

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
        _loading = true;

        Areas.Clear();
        Areas.Add(ScopeChoice.Any);
        foreach (var area in await _areas.AllAsync())
        {
            Areas.Add(new ScopeChoice(area.Id, area.Name));
        }

        Projects.Clear();
        Projects.Add(ScopeChoice.Any);
        Projects.Add(ScopeChoice.None);
        foreach (var project in await _projects.AllAsync())
        {
            Projects.Add(new ScopeChoice(project.Id, project.Outcome));
        }

        Tags.Clear();
        foreach (var tag in await _tags.AllAsync())
        {
            Add(Tags, tag.Id.ToString(), tag.Name);
        }

        Area ??= ScopeChoice.Any;
        Project ??= ScopeChoice.Any;
        Deadline ??= WindowChoice.All[0];
        DoDate ??= WindowChoice.All[0];
        Minutes ??= EstimateChoice.All[0];

        await ReloadFavouritesAsync();

        _loading = false;
        await RunAsync();
    }

    /// <summary>Warunki złożone z tego, co w tej chwili włączone.</summary>
    public FilterQuery Build()
    {
        var conditions = new List<FilterCondition>();

        Attach<TaskState>(conditions, States, FilterCondition.States);
        Attach<Priority>(conditions, Priorities, FilterCondition.Priorities);
        Attach<Energy>(conditions, Energies, FilterCondition.Energies);

        if (Enabled(Tags) is { Length: > 0 } tags)
        {
            conditions.Add(FilterCondition.Tags([.. tags.Select(Guid.Parse)]));
        }

        if (Area?.Id is { } area)
        {
            conditions.Add(FilterCondition.Areas(area));
        }

        if (Project?.Id is { } project)
        {
            conditions.Add(FilterCondition.Projects(project));
        }

        if (Deadline?.Value is { } deadline)
        {
            conditions.Add(FilterCondition.Deadline(deadline));
        }

        if (DoDate?.Value is { } day)
        {
            conditions.Add(FilterCondition.DoDate(day));
        }

        if (Minutes?.Value is { } minutes)
        {
            conditions.Add(FilterCondition.Estimate(minutes));
        }

        if (!string.IsNullOrWhiteSpace(Text))
        {
            conditions.Add(FilterCondition.Contains(Text));
        }

        return new FilterQuery(conditions);
    }

    /// <summary>
    /// Przeliczenie wyników. Jeden przebieg naraz — zob. <see cref="LatestOnly"/>.
    /// </summary>
    [RelayCommand]
    private Task RunAsync() => _loading ? Task.CompletedTask : _queue.RunAsync(RunAsync);

    private async Task RunAsync()
    {
        var today = _clock.Today;

        Results.Clear();
        foreach (var task in await _filters.RunAsync(Build()))
        {
            Results.Add(TaskRow.From(task, today));
        }

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(Empty));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>Wczytanie zapisanego widoku do konstruktora.</summary>
    [RelayCommand]
    private async Task OpenAsync(SavedFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        if (filter.Query is not { } query)
        {
            // Widok zapisany w wersji, której ta nie rozumie. Zostaje w Ulubionych
            // z nazwą — ale kliknięcie musi powiedzieć, dlaczego nic się nie stało,
            // inaczej ekran wygląda na zepsuty.
            Status = $"Widok „{filter.Name}” zapisano w postaci, "
                + "której ta wersja nie czyta. Ułóż go na nowo.";
            OnPropertyChanged(nameof(HasStatus));
            return;
        }

        Status = string.Empty;
        OnPropertyChanged(nameof(HasStatus));

        _loading = true;

        Clear();
        OpenId = filter.Id;
        Name = filter.Name;

        foreach (var condition in query.Conditions)
        {
            Apply(condition);
        }

        _loading = false;

        OnPropertyChanged(nameof(IsSaved));
        await RunAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var query = Build();

        if (query.IsEmpty || string.IsNullOrWhiteSpace(Name))
        {
            return;
        }

        if (OpenId == Guid.Empty)
        {
            OpenId = (await _filters.SaveAsync(Name, query)).Id;
        }
        else
        {
            await _filters.UpdateAsync(OpenId, Name, query);
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
        _loading = true;
        Clear();
        _loading = false;

        OnPropertyChanged(nameof(IsSaved));
        await RunAsync();
    }

    private async Task ReloadFavouritesAsync()
    {
        Favourites.Clear();
        foreach (var filter in await _filters.FavouritesAsync())
        {
            Favourites.Add(filter);
        }

        OnPropertyChanged(nameof(HasFavourites));
    }

    private void Clear()
    {
        OpenId = Guid.Empty;
        Name = string.Empty;
        Text = string.Empty;
        Area = ScopeChoice.Any;
        Project = ScopeChoice.Any;
        Deadline = WindowChoice.All[0];
        DoDate = WindowChoice.All[0];
        Minutes = EstimateChoice.All[0];

        foreach (var toggle in States.Concat(Priorities).Concat(Energies).Concat(Tags))
        {
            toggle.IsOn = false;
        }
    }

    private void Apply(FilterCondition condition)
    {
        switch (condition.Field)
        {
            case FilterField.State:
                Enable(States, condition.Values);
                break;
            case FilterField.Priority:
                Enable(Priorities, condition.Values);
                break;
            case FilterField.Energy:
                Enable(Energies, condition.Values);
                break;
            case FilterField.Tag:
                Enable(Tags, condition.Values);
                break;
            case FilterField.Area:
                Area = Find(Areas, condition.Values);
                break;
            case FilterField.Project:
                Project = Find(Projects, condition.Values);
                break;
            case FilterField.Deadline:
                Deadline = WindowChoice.All.FirstOrDefault(w => w.Value == condition.Window);
                break;
            case FilterField.DoDate:
                DoDate = WindowChoice.All.FirstOrDefault(w => w.Value == condition.Window);
                break;
            case FilterField.Estimate:
                // Widok zapisany na urządzeniu z inną listą minut ma się otworzyć,
                // a nie zniknąć: brakująca wartość dokładana do listy na miejscu.
                Minutes = EstimateChoice.All.FirstOrDefault(m => m.Value == condition.MaxMinutes)
                    ?? new EstimateChoice(condition.MaxMinutes, $"{condition.MaxMinutes} min");
                break;
            case FilterField.Text:
                Text = condition.Text ?? string.Empty;
                break;
        }
    }

    private static void Enable(
        IEnumerable<FilterToggle> toggles, IReadOnlyList<string> values)
    {
        foreach (var toggle in toggles)
        {
            toggle.IsOn = values.Contains(toggle.Value);
        }
    }

    /// <summary>
    /// Wartość, której już nie ma — skasowany obszar, projekt z drugiego urządzenia —
    /// wraca jako „dowolny", a nie jako pusta pozycja bez nazwy.
    /// </summary>
    private static ScopeChoice? Find(
        IEnumerable<ScopeChoice> rows, IReadOnlyList<string> values) =>
        rows.FirstOrDefault(p => p.Id is { } id && values.Contains(id.ToString()))
        ?? ScopeChoice.Any;

    private static string[] Enabled(IEnumerable<FilterToggle> toggles) =>
        toggles.Where(p => p.IsOn).Select(p => p.Value).ToArray();

    private static void Attach<T>(
        List<FilterCondition> conditions,
        IEnumerable<FilterToggle> toggles,
        Func<T[], FilterCondition> build)
        where T : struct, Enum
    {
        if (Enabled(toggles) is { Length: > 0 } values)
        {
            conditions.Add(build([.. values.Select(Enum.Parse<T>)]));
        }
    }

    private void Fill(
        ObservableCollection<FilterToggle> where, IEnumerable<(string Value, string Label)> co)
    {
        foreach (var (value, label) in co)
        {
            Add(where, value, label);
        }
    }

    /// <summary>
    /// Przełącznik podpięty do przeliczania. Zdarzenie, nie powiązanie po stronie okna:
    /// inaczej każdy nowy ekran z tymi samymi przełącznikami musiałby pamiętać o tym,
    /// żeby je podpiąć — a zapomnienie nie daje żadnego objawu poza listą, która
    /// milczy.
    /// </summary>
    private void Add(ObservableCollection<FilterToggle> where, string value, string label)
    {
        var toggle = new FilterToggle(label, value);
        toggle.PropertyChanged += async (_, _) => await RunAsync();
        where.Add(toggle);
    }

    partial void OnAreaChanged(ScopeChoice? value) => _ = RunAsync();

    partial void OnProjectChanged(ScopeChoice? value) => _ = RunAsync();

    partial void OnDeadlineChanged(WindowChoice? value) => _ = RunAsync();

    partial void OnDoDateChanged(WindowChoice? value) => _ = RunAsync();

    partial void OnMinutesChanged(EstimateChoice? value) => _ = RunAsync();

    partial void OnTextChanged(string value) => _ = RunAsync();

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));
}
