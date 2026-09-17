using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Areas;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Szczegół zadania: termin, dzień wykonania, przypomnienie i rytm (etap 4).
/// </summary>
/// <remarks>
/// Jedno wejście do wszystkiego, co etap 4 dołożył do modelu. Osobny ekran na każdą
/// z tych rzeczy oznaczałby cztery miejsca do znalezienia zamiast jednego, a wszystkie
/// cztery dotyczą tej samej decyzji: kiedy to ma się zdarzyć.
/// </remarks>
public sealed partial class TaskDetailViewModel(
    TaskEditService edit, IClock clock, IAreaRepository areas, InboxService inbox,
    IActivityLog log)
    : ObservableObject
{
    private Guid _id;
    private bool _loading;

    /// <summary>Ile trwa zadanie z godziną, ale bez podanego końca (spec 11).</summary>
    private const int DomyslneMinuty = 30;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? DoDate { get; set; }

    /// <summary>
    /// Godzina rozpoczęcia i zakończenia.
    /// </summary>
    /// <remarks>
    /// Koniec nie jest osobnym polem w modelu: zadanie ma oszacowanie długości i to
    /// ono rysuje blok na siatce. Koniec jest więc innym sposobem powiedzenia tego
    /// samego — wpisanie go przelicza się na minuty, a brak zostawia trzydzieści.
    /// Dwie prawdy o jednej rzeczy rozjechałyby się przy pierwszej zmianie jednej z nich.
    /// </remarks>
    [ObservableProperty]
    public partial TimeSpan? DoTime { get; set; }

    [ObservableProperty]
    public partial TimeSpan? EndTime { get; set; }

    /// <summary>Obszar. Pusty znaczy „jeszcze nierozstrzygnięty" — tak wygląda wrzut.</summary>
    [ObservableProperty]
    public partial Area? SelectedArea { get; set; }

    public ObservableCollection<Area> Areas { get; } = [];

    /// <summary>
    /// Czy pokazywać resztę pól.
    /// </summary>
    /// <remarks>
    /// Okno miało dwanaście pól jedno pod drugim i trzeba było przewijać, żeby dojść
    /// do przycisku zapisu. Widoczne zostaje to, co wypełnia się przy **każdym**
    /// zadaniu — nazwa, dzień, godziny — a rzadkie schodzą pod jeden przełącznik.
    /// Pole, które w dziewięciu zadaniach na dziesięć zostaje puste, jest w oknie
    /// kosztem, a nie możliwością.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowMore { get; set; }

    /// <summary>Podsumowanie schowanego: żeby zwinięte nie znaczyło „nie wiadomo co tam jest".</summary>
    public string MoreSummary
    {
        get
        {
            var czesci = new List<string>();

            if (Deadline is { } termin)
            {
                czesci.Add($"termin {termin:dd.MM}");
            }

            if (ReminderDay is not null)
            {
                czesci.Add("przypomnienie");
            }

            if (Waga != Priority.None)
            {
                czesci.Add(SelectedPriority!.Label);
            }

            if (Sila != Energy.Unknown)
            {
                czesci.Add(SelectedEnergyLevel!.Label);
            }

            if (Rytm is not null)
            {
                czesci.Add("powtarza się");
            }

            return czesci.Count == 0 ? "termin, przypomnienie, waga, energia, rytm"
                : string.Join(" · ", czesci);
        }
    }

    /// <summary>Co poszło nie tak przy zapisie. Puste, gdy poszło.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    [ObservableProperty]
    public partial DateTimeOffset? Deadline { get; set; }

    /// <summary>
    /// Dzień i pora przypomnienia osobno, bo wybiera się je osobno. Złożenie w chwilę
    /// dzieje się przy zapisie — <see cref="ReminderAt"/>.
    /// </summary>
    [ObservableProperty]
    public partial DateTimeOffset? ReminderDay { get; set; }

    [ObservableProperty]
    public partial TimeSpan? ReminderTime { get; set; }

    [ObservableProperty]
    public partial PriorityChoice? SelectedPriority { get; set; } = PriorityChoice.All[0];

    /// <summary>Liczba dziesiętna z tego samego powodu co odstęp rytmu: NumericUpDown.</summary>
    [ObservableProperty]
    public partial decimal? EstimatedMinutes { get; set; }

    [ObservableProperty]
    public partial EnergyLevelChoice? SelectedEnergyLevel { get; set; } = EnergyLevelChoice.All[0];

    // --- rytm ----------------------------------------------------------------

    [ObservableProperty]
    public partial RepeatChoice? SelectedRepeat { get; set; } = RepeatChoice.All[0];

    /// <summary>
    /// Liczby dziesiętne, nie całkowite — <c>NumericUpDown</c> operuje na
    /// <see cref="decimal"/>, a powiązanie kompilowane nie zamienia typu za nas.
    /// Zamiana na liczbę całkowitą dzieje się przy budowaniu reguły.
    /// </summary>
    [ObservableProperty]
    public partial decimal Interval { get; set; } = 1;

    [ObservableProperty]
    public partial bool Monday { get; set; }

    [ObservableProperty]
    public partial bool Tuesday { get; set; }

    [ObservableProperty]
    public partial bool Wednesday { get; set; }

    [ObservableProperty]
    public partial bool Thursday { get; set; }

    [ObservableProperty]
    public partial bool Friday { get; set; }

    [ObservableProperty]
    public partial bool Saturday { get; set; }

    [ObservableProperty]
    public partial bool Sunday { get; set; }

    [ObservableProperty]
    public partial decimal? DayOfMonth { get; set; }

    [ObservableProperty]
    public partial AnchorChoice? SelectedAnchor { get; set; } = AnchorChoice.All[0];

    [ObservableProperty]
    public partial MissedChoice? SelectedMissed { get; set; } = MissedChoice.All[0];

    public IReadOnlyList<RepeatChoice> Repeats => RepeatChoice.All;

    public IReadOnlyList<AnchorChoice> Anchors => AnchorChoice.All;

    public IReadOnlyList<MissedChoice> Missed => MissedChoice.All;

    public IReadOnlyList<PriorityChoice> Priorities => PriorityChoice.All;

    public IReadOnlyList<EnergyLevelChoice> Energies => EnergyLevelChoice.All;

    /// <summary>
    /// Rytm zdaniem, nie formularzem. Sześć pól da się wypełnić źle i nie zauważyć;
    /// zdanie da się przeczytać i od razu wiedzieć, czy to jest to, o co chodziło.
    /// </summary>
    public string Summary
    {
        get
        {
            var regula = BuildRule(out var problem);

            // Reguła niepełna nie może pokazywać się jako „nie powtarza się" — wybrałaś
            // „co tydzień", a zdanie mówiłoby, że rytmu nie ma. Zdanie musi mówić prawdę
            // o tym, co jest na ekranie, także wtedy, gdy na ekranie czegoś brakuje.
            return problem is not null ? problem : RecurrenceText.Describe(regula);
        }
    }

    /// <summary>
    /// Wybory z list, czytane odpornie na puste.
    /// </summary>
    /// <remarks>
    /// Pola wyboru **mogą** nie mieć nic wybranego — kontrolka potrafi wpisać pustkę
    /// przy składaniu i przy podmianie listy — a typ mówił, że nie mogą. Sięgnięcie
    /// po <c>.Kind</c> na pustce rzucało wyjątek przed pierwszą linijką, którą cokolwiek
    /// zapisuje: polecenie zapisu kończyło się **niczym**. Bez zapisu, bez komunikatu,
    /// bez wpisu w dzienniku, z otwartym oknem wyglądającym jak przed kliknięciem.
    /// </remarks>
    private RecurrenceKind? Rytm => SelectedRepeat?.Kind;

    private Priority Waga => SelectedPriority?.Value ?? Priority.None;

    private Energy Sila => SelectedEnergyLevel?.Value ?? Energy.Unknown;

    public bool IsRepeating => Rytm is not null;

    public bool NeedsInterval => Rytm
        is RecurrenceKind.EveryNDays or RecurrenceKind.Weekly
        or RecurrenceKind.Monthly or RecurrenceKind.Yearly;

    public bool NeedsWeekdays => Rytm == RecurrenceKind.Weekly;

    public bool NeedsDayOfMonth => Rytm == RecurrenceKind.Monthly;

    public event EventHandler? Saved;

    /// <summary>
    /// Otwarcie szczegółu. Obszary dociągane przy każdym otwarciu, bo lista bywa
    /// zmieniana na osobnym ekranie i zapamiętana zrobiłaby się nieprawdziwa.
    /// </summary>
    public async Task LoadAsync(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var czynne = await areas.ActiveAsync();

        Areas.Clear();
        foreach (var obszar in czynne)
        {
            Areas.Add(obszar);
        }

        Load(task);
    }

    /// <summary>
    /// Nowe zadanie na wskazany dzień i godzinę — stąd otwiera je kliknięcie w pustą
    /// siatkę kalendarza.
    /// </summary>
    /// <remarks>
    /// Zadanie powstaje dopiero przy zapisie, nie przy otwarciu okna. Utworzone
    /// z góry zostawiałoby po zamknięciu bez zapisu pusty wpis w skrzynce — czyli
    /// karę za rozmyślenie się.
    /// </remarks>
    public async Task NewAsync(DateOnly day, TimeOnly time)
    {
        var czynne = await areas.ActiveAsync();

        Areas.Clear();
        foreach (var obszar in czynne)
        {
            Areas.Add(obszar);
        }

        _loading = true;
        _id = Guid.Empty;
        Problem = null;
        OnPropertyChanged(nameof(HasProblem));

        Title = string.Empty;
        Note = string.Empty;
        Deadline = null;
        ReminderDay = null;
        ReminderTime = null;
        SelectedPriority = Priorities[0];
        SelectedEnergyLevel = Energies[0];
        EstimatedMinutes = null;
        SelectedArea = Areas.FirstOrDefault();
        LoadRule(null);

        DoDate = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), clock.Now.Offset);
        DoTime = time.ToTimeSpan();
        EndTime = time.ToTimeSpan() + TimeSpan.FromMinutes(DomyslneMinuty);

        _loading = false;
        ShowMore = false;
        Refresh();
        IsOpen = true;
    }

    public void Load(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        _loading = true;
        Problem = null;
        OnPropertyChanged(nameof(HasProblem));
        _id = task.Id;
        Title = task.Title;
        Note = task.Note ?? string.Empty;
        DoDate = ToOffset(task.DoDate);
        Deadline = ToOffset(task.Deadline);
        ReminderDay = task.ReminderAt is { } r ? ToOffset(DateOnly.FromDateTime(r.DateTime)) : null;
        ReminderTime = task.ReminderAt?.TimeOfDay;
        SelectedPriority = Priorities.First(p => p.Value == task.Priority);
        EstimatedMinutes = task.EstimatedMinutes;
        DoTime = task.DoTime?.ToTimeSpan();
        SelectedArea = Areas.FirstOrDefault(o => o.Id == task.AreaId);

        // Koniec z początku i długości — nie ma go w modelu, bo byłby drugą prawdą
        // o tej samej rzeczy.
        EndTime = task.DoTime is { } poczatek
            ? poczatek.ToTimeSpan() + TimeSpan.FromMinutes(task.EstimatedMinutes ?? DomyslneMinuty)
            : null;
        SelectedEnergyLevel = Energies.First(e => e.Value == task.Energy);
        LoadRule(task.Recurrence);

        _loading = false;
        ShowMore = Deadline is not null
            || ReminderDay is not null
            || Rytm is not null
            || Waga != Priority.None;

        Refresh();
        IsOpen = true;
    }

    private void LoadRule(RecurrenceRule? rule)
    {
        SelectedRepeat = Repeats.First(r => r.Kind == rule?.Kind);
        Interval = rule?.Interval ?? 1;
        DayOfMonth = rule?.DayOfMonth;
        SelectedAnchor = Anchors.First(a => a.Value == (rule?.Anchor ?? RecurrenceAnchor.FromScheduled));

        // Accumulate nie ma pozycji na liście (spec 13.2 pkt 1), więc zadanie
        // przyniesione z drugiego urządzenia pokazuje się jako Carry. Zapisanie
        // szczegółu zmieni je na Carry — i to jest zamierzone, bo tak brzmi to,
        // co widać na ekranie.
        SelectedMissed = Missed.FirstOrDefault(m => m.Value == rule?.OnMissed) ?? Missed[0];

        var dni = rule?.DaysOfWeek ?? Weekdays.None;
        Monday = dni.Includes(DayOfWeek.Monday);
        Tuesday = dni.Includes(DayOfWeek.Tuesday);
        Wednesday = dni.Includes(DayOfWeek.Wednesday);
        Thursday = dni.Includes(DayOfWeek.Thursday);
        Friday = dni.Includes(DayOfWeek.Friday);
        Saturday = dni.Includes(DayOfWeek.Saturday);
        Sunday = dni.Includes(DayOfWeek.Sunday);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private async Task SaveAsync()
    {
        // Ślad **przed** wszystkim innym. Do dziś pierwszą rzeczą w tym poleceniu było
        // budowanie reguły rytmu, i to poza blokiem chroniącym: wyjątek stamtąd nie
        // miał dokąd trafić, bo polecenie wołane jest bez oczekiwania na wynik.
        await log.RecordAsync("Zadanie: polecenie zapisu", Title);

        string? problem;
        RecurrenceRule? regula;

        try
        {
            regula = BuildRule(out problem);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Zadanie: zapis", Title, ActivityLevel.Problem,
                $"budowanie rytmu: {e.GetType().Name}: {e.Message}");

            return;
        }

        if (problem is not null)
        {
            // Do dziś była tu cicha odmowa: przycisk klikał, okno zostawało otwarte
            // i nic nie mówiło dlaczego. Zdanie podsumowujące rytm bywa niżej,
            // poza widokiem, więc powód idzie także tutaj.
            Problem = problem;
            OnPropertyChanged(nameof(HasProblem));
            await log.RecordAsync("Zadanie: zapis", Title, ActivityLevel.Problem, problem);
            return;
        }

        try
        {
            if (_id == Guid.Empty)
            {
                if (string.IsNullOrWhiteSpace(Title))
                {
                    Problem = "Nowe zadanie potrzebuje nazwy.";
                    OnPropertyChanged(nameof(HasProblem));

                    await log.RecordAsync(
                        "Zadanie: zapis", "bez nazwy", ActivityLevel.Problem,
                        "Nowe zadanie zapisane bez nazwy nie powstaje.");

                    return;
                }

                _id = await inbox.CaptureAsync(Title);
            }

            await edit.ApplyAsync(_id, new TaskEdit(
                Title,
                string.IsNullOrWhiteSpace(Note) ? null : Note,
                ToDate(DoDate),
                ToDate(Deadline),
                ReminderAt(),
                regula,
                Waga,
                Minuty(),
                Sila,
                SelectedArea?.Id,
                DoTime is { } pora ? TimeOnly.FromTimeSpan(pora) : null));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // **Każdy** wyjątek, nie wybrane rodzaje. Polecenie wołane jest bez
            // oczekiwania na wynik, więc rodzaj spoza listy nie miał dokąd trafić:
            // znikał bez śladu, a okno zostawało otwarte bez słowa wyjaśnienia.
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Zadanie: zapis", Title, ActivityLevel.Problem,
                $"{e.GetType().Name}: {e.Message}");

            return;
        }

        // Ślad także po udanym zapisie: „zapisało się, ale nie widać" i „nie zapisało
        // się" wyglądają na ekranie tak samo, a to dwie różne rzeczy do zrobienia.
        await log.RecordAsync(
            "Zadanie: zapis",
            $"{Title} — dzień {ToDate(DoDate)?.ToString("yyyy-MM-dd") ?? "brak"}, "
                + $"godzina {(DoTime is { } g ? g.ToString(@"hh\:mm") : "brak")}, "
                + $"obszar {SelectedArea?.Name ?? "brak"}");

        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Długość zadania w minutach.
    /// </summary>
    /// <remarks>
    /// Godzina zakończenia jest ważniejsza od ręcznego oszacowania, bo została wpisana
    /// przy tym samym dniu i tej samej godzinie — a oszacowanie mogło zostać z innego
    /// planu. Koniec przed początkiem znaczy przejście przez północ.
    /// </remarks>
    private int? Minuty()
    {
        if (DoTime is { } poczatek && EndTime is { } koniec)
        {
            var dlugosc = koniec > poczatek
                ? koniec - poczatek
                : koniec + TimeSpan.FromDays(1) - poczatek;

            return Math.Max(1, (int)dlugosc.TotalMinutes);
        }

        return EstimatedMinutes is { } minuty ? (int)minuty : null;
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        await edit.CompleteAsync(_id);
        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Chwila przypomnienia z dnia i pory. Sam dzień bez pory znaczy rano — ale
    /// **sama pora bez dnia nie znaczy nic**, bo nie wiadomo którego. Wtedy przypomnienia
    /// nie ma, zamiast zgadywać dzisiaj i odezwać się natychmiast.
    /// </summary>
    private DateTimeOffset? ReminderAt()
    {
        if (ReminderDay is not { } dzien)
        {
            return null;
        }

        var pora = ReminderTime ?? new TimeSpan(8, 0, 0);
        return new DateTimeOffset(dzien.Date.Add(pora), clock.Now.Offset);
    }

    /// <summary>
    /// Reguła z tego, co na ekranie, albo <c>null</c> z powodem. Ten sam kod służy
    /// do zapisu i do zdania podsumowującego, więc podsumowanie nie może pokazywać
    /// czegoś innego niż to, co się zapisze.
    /// </summary>
    private RecurrenceRule? BuildRule(out string? problem)
    {
        problem = null;

        if (Rytm is not { } rodzaj)
        {
            return null;
        }

        var dni = Weekdays.None;
        if (Monday) { dni |= Weekdays.Monday; }
        if (Tuesday) { dni |= Weekdays.Tuesday; }
        if (Wednesday) { dni |= Weekdays.Wednesday; }
        if (Thursday) { dni |= Weekdays.Thursday; }
        if (Friday) { dni |= Weekdays.Friday; }
        if (Saturday) { dni |= Weekdays.Saturday; }
        if (Sunday) { dni |= Weekdays.Sunday; }

        try
        {
            return new RecurrenceRule(
                rodzaj,
                (int)Math.Max(1, Interval),
                dni,
                rodzaj == RecurrenceKind.Monthly && DayOfMonth is { } dzien ? (int)dzien : null,
                SelectedAnchor?.Value ?? RecurrenceRule.DefaultAnchorFor(rodzaj),
                SelectedMissed?.Value ?? OnMissed.Carry);
        }
        catch (ArgumentException)
        {
            // Jedyny przypadek, jaki może tu wystąpić: rytm tygodniowy bez wskazanego
            // dnia. Komunikat z wyjątku niesie nazwę parametru, więc nie nadaje się
            // na ekran.
            problem = "co tydzień — ale w które dni?";
            return null;
        }
    }

    private static DateTimeOffset? ToOffset(DateOnly? date) =>
        date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;

    private static DateOnly? ToDate(DateTimeOffset? value) =>
        value is { } v ? DateOnly.FromDateTime(v.Date) : null;

    private void Refresh()
    {
        OnPropertyChanged(nameof(MoreSummary));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsRepeating));
        OnPropertyChanged(nameof(NeedsInterval));
        OnPropertyChanged(nameof(NeedsWeekdays));
        OnPropertyChanged(nameof(NeedsDayOfMonth));
    }

    partial void OnSelectedRepeatChanged(RepeatChoice? value)
    {
        if (_loading)
        {
            return;
        }

        // Domyślne zaczepienie wynika z rodzaju (spec 5.7), więc zmiana rodzaju ma je
        // przestawić. Zostawione ręcznie ustawione dałoby „co poniedziałek, licząc od
        // wykonania" jako stan domyślny — czyli rytm dryfujący na środy.
        if (value?.Kind is { } rodzaj)
        {
            SelectedAnchor = Anchors.First(a => a.Value == RecurrenceRule.DefaultAnchorFor(rodzaj));
        }

        Refresh();
    }

    partial void OnIntervalChanged(decimal value) => Refresh();

    partial void OnDayOfMonthChanged(decimal? value) => Refresh();

    partial void OnSelectedAnchorChanged(AnchorChoice? value) => Refresh();

    partial void OnSelectedMissedChanged(MissedChoice? value) => Refresh();

    partial void OnMondayChanged(bool value) => Refresh();

    partial void OnTuesdayChanged(bool value) => Refresh();

    partial void OnWednesdayChanged(bool value) => Refresh();

    partial void OnThursdayChanged(bool value) => Refresh();

    partial void OnFridayChanged(bool value) => Refresh();

    partial void OnSaturdayChanged(bool value) => Refresh();

    partial void OnSundayChanged(bool value) => Refresh();
}
