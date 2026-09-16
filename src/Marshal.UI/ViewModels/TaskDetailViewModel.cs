using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
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
public sealed partial class TaskDetailViewModel(TaskEditService edit, IClock clock) : ObservableObject
{
    private Guid _id;
    private bool _loading;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? DoDate { get; set; }

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
    public partial PriorityChoice SelectedPriority { get; set; } = PriorityChoice.All[0];

    // --- rytm ----------------------------------------------------------------

    [ObservableProperty]
    public partial RepeatChoice SelectedRepeat { get; set; } = RepeatChoice.All[0];

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
    public partial AnchorChoice SelectedAnchor { get; set; } = AnchorChoice.All[0];

    [ObservableProperty]
    public partial MissedChoice SelectedMissed { get; set; } = MissedChoice.All[0];

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public IReadOnlyList<RepeatChoice> Repeats => RepeatChoice.All;

    public IReadOnlyList<AnchorChoice> Anchors => AnchorChoice.All;

    public IReadOnlyList<MissedChoice> Missed => MissedChoice.All;

    public IReadOnlyList<PriorityChoice> Priorities => PriorityChoice.All;

    /// <summary>
    /// Rytm zdaniem, nie formularzem. Sześć pól da się wypełnić źle i nie zauważyć;
    /// zdanie da się przeczytać i od razu wiedzieć, czy to jest to, o co chodziło.
    /// </summary>
    public string Summary => RecurrenceText.Describe(BuildRule(out _));

    public bool IsRepeating => SelectedRepeat.Kind is not null;

    public bool NeedsInterval => SelectedRepeat.Kind
        is RecurrenceKind.EveryNDays or RecurrenceKind.Weekly
        or RecurrenceKind.Monthly or RecurrenceKind.Yearly;

    public bool NeedsWeekdays => SelectedRepeat.Kind == RecurrenceKind.Weekly;

    public bool NeedsDayOfMonth => SelectedRepeat.Kind == RecurrenceKind.Monthly;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public event EventHandler? Saved;

    public void Load(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        _loading = true;
        _id = task.Id;
        Title = task.Title;
        Note = task.Note ?? string.Empty;
        DoDate = ToOffset(task.DoDate);
        Deadline = ToOffset(task.Deadline);
        ReminderDay = task.ReminderAt is { } r ? ToOffset(DateOnly.FromDateTime(r.DateTime)) : null;
        ReminderTime = task.ReminderAt?.TimeOfDay;
        SelectedPriority = Priorities.First(p => p.Value == task.Priority);
        Problem = null;

        LoadRule(task.Recurrence);

        _loading = false;
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
        var regula = BuildRule(out var problem);

        if (problem is not null)
        {
            Problem = problem;
            return;
        }

        await edit.ApplyAsync(_id, new TaskEdit(
            Title,
            string.IsNullOrWhiteSpace(Note) ? null : Note,
            ToDate(DoDate),
            ToDate(Deadline),
            ReminderAt(),
            regula,
            SelectedPriority.Value));

        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
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

        if (SelectedRepeat.Kind is not { } rodzaj)
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
                SelectedAnchor.Value,
                SelectedMissed.Value);
        }
        catch (ArgumentException e)
        {
            problem = e.Message;
            return null;
        }
    }

    private static DateTimeOffset? ToOffset(DateOnly? date) =>
        date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;

    private static DateOnly? ToDate(DateTimeOffset? value) =>
        value is { } v ? DateOnly.FromDateTime(v.Date) : null;

    private void Refresh()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsRepeating));
        OnPropertyChanged(nameof(NeedsInterval));
        OnPropertyChanged(nameof(NeedsWeekdays));
        OnPropertyChanged(nameof(NeedsDayOfMonth));
    }

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSelectedRepeatChanged(RepeatChoice value)
    {
        if (_loading)
        {
            return;
        }

        // Domyślne zaczepienie wynika z rodzaju (spec 5.7), więc zmiana rodzaju ma je
        // przestawić. Zostawione ręcznie ustawione dałoby „co poniedziałek, licząc od
        // wykonania" jako stan domyślny — czyli rytm dryfujący na środy.
        if (value.Kind is { } rodzaj)
        {
            SelectedAnchor = Anchors.First(a => a.Value == RecurrenceRule.DefaultAnchorFor(rodzaj));
        }

        Refresh();
    }

    partial void OnIntervalChanged(decimal value) => Refresh();

    partial void OnDayOfMonthChanged(decimal? value) => Refresh();

    partial void OnSelectedAnchorChanged(AnchorChoice value) => Refresh();

    partial void OnSelectedMissedChanged(MissedChoice value) => Refresh();

    partial void OnMondayChanged(bool value) => Refresh();

    partial void OnTuesdayChanged(bool value) => Refresh();

    partial void OnWednesdayChanged(bool value) => Refresh();

    partial void OnThursdayChanged(bool value) => Refresh();

    partial void OnFridayChanged(bool value) => Refresh();

    partial void OnSaturdayChanged(bool value) => Refresh();

    partial void OnSundayChanged(bool value) => Refresh();
}
