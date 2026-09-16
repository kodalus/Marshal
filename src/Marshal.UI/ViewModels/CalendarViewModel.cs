using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Wpis siatki przeliczony na punkty.
/// </summary>
/// <remarks>
/// Przeliczenie siedzi w warstwie okna, bo punkty są pojęciem okna. Warstwa aplikacji
/// oddaje godziny i numer kolumny — to, ile to ma pikseli, zależy od tego, czy patrzysz
/// na dzień, czy na tydzień.
/// </remarks>
public sealed record SlotBox(
    string Title, double Top, double Height, double Left, double Width, bool IsTask)
{
    /// <summary>Zadanie półprzezroczyste: umowa z kimś i zamiar wobec siebie to nie to samo.</summary>
    public double Opacity => IsTask ? 0.55 : 1.0;
}

/// <summary>Jeden dzień siatki gotowy do narysowania.</summary>
public sealed record CalendarColumn(
    DateOnly Date,
    string Header,
    IReadOnlyList<string> AllDay,
    IReadOnlyList<SlotBox> Slots)
{
    public bool HasAllDay => AllDay.Count > 0;

    public string AllDayText => string.Join("  ·  ", AllDay);
}

/// <summary>
/// Kalendarz godzinowy: dzień, trzy dni, tydzień (spec 11).
/// </summary>
public sealed partial class CalendarViewModel(
    CalendarSyncService calendar, IClock clock) : ObservableObject
{
    /// <summary>Wysokość godziny w punktach.</summary>
    /// <remarks>
    /// Czterdzieści osiem, bo przy dwudziestu czterech godzinach daje to siatkę, którą
    /// da się przewinąć jednym ruchem, a półgodzinne spotkanie ma jeszcze na czym
    /// pokazać tytuł.
    /// </remarks>
    private const double HourHeight = 48;

    private static readonly string[] DayNames =
        ["pon", "wt", "śr", "czw", "pt", "sob", "niedz"];

    [ObservableProperty]
    public partial DateOnly Anchor { get; set; }

    [ObservableProperty]
    public partial int VisibleDays { get; set; } = 3;

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public ObservableCollection<CalendarColumn> Columns { get; } = [];

    public double GridHeight => 24 * HourHeight;

    /// <summary>Szerokość kolumny dnia. Im więcej dni, tym węziej — i to jest cała różnica.</summary>
    public double ColumnWidth => VisibleDays switch
    {
        1 => 520,
        3 => 240,
        _ => 130,
    };

    public IReadOnlyList<string> HourLabels =>
        [.. Enumerable.Range(0, 24).Select(h => $"{h:00}:00")];

    public string Range => VisibleDays == 1
        ? $"{Anchor:yyyy-MM-dd}"
        : $"{Anchor:yyyy-MM-dd} — {Anchor.AddDays(VisibleDays - 1):yyyy-MM-dd}";

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public async Task LoadAsync()
    {
        if (Anchor == default)
        {
            GoToToday();
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var dni = await calendar.AgendaAsync(Anchor, VisibleDays);

        Columns.Clear();
        foreach (var dzien in dni)
        {
            Columns.Add(new CalendarColumn(
                dzien.Date,
                $"{DayNames[((int)dzien.Date.DayOfWeek + 6) % 7]} {dzien.Date.Day}",
                dzien.AllDay.Select(e => e.Title).ToList(),
                dzien.Timed.Select(Box).ToList()));
        }

        OnPropertyChanged(nameof(Range));
    }

    /// <summary>
    /// Pobranie świeżych wydarzeń na żądanie. Osobno od przerysowania, bo to dwie różne
    /// rzeczy: siatkę składamy z tego, co w bazie, a sieć bywa niedostępna.
    /// </summary>
    [RelayCommand]
    private async Task FetchAsync()
    {
        var raport = await calendar.RefreshAsync(force: true);

        Problem = raport.Failed > 0
            ? $"Nie udało się odświeżyć {raport.Failed} z {raport.Failed + raport.Sources} kalendarzy."
            : null;

        OnPropertyChanged(nameof(HasProblem));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        Anchor = Anchor.AddDays(-VisibleDays);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        Anchor = Anchor.AddDays(VisibleDays);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task TodayAsync()
    {
        GoToToday();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SetDaysAsync(int days)
    {
        VisibleDays = days is 1 or 3 or 7 ? days : 3;

        // Tydzień zaczyna się w poniedziałek, a nie „od dziś przez siedem dni":
        // tydzień, który zaczyna się w środę, nie wygląda jak tydzień.
        if (VisibleDays == 7)
        {
            Anchor = Anchor.AddDays(-(((int)Anchor.DayOfWeek + 6) % 7));
        }

        OnPropertyChanged(nameof(ColumnWidth));
        await RefreshAsync();
    }

    private void GoToToday()
    {
        Anchor = DateOnly.FromDateTime(clock.Now.DateTime);

        if (VisibleDays == 7)
        {
            Anchor = Anchor.AddDays(-(((int)Anchor.DayOfWeek + 6) % 7));
        }
    }

    private SlotBox Box(AgendaSlot slot)
    {
        var szerokosc = ColumnWidth / Math.Max(1, slot.Columns);

        return new SlotBox(
            slot.Entry.Title,
            slot.Entry.StartHour * HourHeight,
            slot.Entry.Hours * HourHeight,
            slot.Column * szerokosc,
            szerokosc - 2,
            slot.Entry.Kind == AgendaKind.Task);
    }
}
