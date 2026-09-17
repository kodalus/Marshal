using System.Collections.ObjectModel;
using Avalonia.Media;
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
    string Title, double Top, double Height, double Left, double Width, bool IsTask, string? Color)
{
    /// <summary>Barwa dla wpisu bez własnej. Zadanie inne niż wydarzenie, żeby dało się je odróżnić.</summary>
    private const string DomyslneWydarzenie = "#6C8FBF";

    private const string DomyslneZadanie = "#909090";

    /// <summary>Zadanie półprzezroczyste: umowa z kimś i zamiar wobec siebie to nie to samo.</summary>
    public double Opacity => IsTask ? 0.55 : 1.0;

    /// <summary>
    /// Barwa kalendarza albo zadania, przyciemniona przezroczystością.
    /// </summary>
    /// <remarks>
    /// Barwy z Google to jasne pastele, a okno bywa ciemne — położone wprost dawałyby
    /// jasny prostokąt z jasnym napisem. Ta sama barwa z przezroczystością zachowuje
    /// odcień, po którym poznaje się kalendarz, i zostawia tekst czytelnym w obu motywach.
    /// Liczenie tutaj, a nie konwerterem: konwerter to trzecie miejsce do zajrzenia
    /// przy czytaniu jednego wiersza XAML-a.
    /// </remarks>
    public IBrush Background
    {
        get
        {
            var zrodlo = string.IsNullOrWhiteSpace(Color)
                ? IsTask ? DomyslneZadanie : DomyslneWydarzenie
                : Color;

            return Avalonia.Media.Color.TryParse(zrodlo, out var barwa)
                ? new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x66, barwa.R, barwa.G, barwa.B))
                : new SolidColorBrush(Avalonia.Media.Color.Parse(DomyslneWydarzenie));
        }
    }
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

    /// <summary>
    /// Co jest w bazie, a co w tym zakresie.
    /// </summary>
    /// <remarks>
    /// Pusta siatka ma trzy różne przyczyny — nic nie pobrano, pobrano nie na te dni,
    /// albo pobrano i nie narysowano — a wyglądają identycznie. Ta jedna linijka
    /// rozdziela je bez zgadywania i bez kabla.
    /// </remarks>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

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

    /// <summary>
    /// Prośba o przewinięcie siatki, w punktach od północy.
    /// </summary>
    /// <remarks>
    /// Model widoku nie sięga do okna, a przewijanie jest rzeczą okna: tu jest tylko
    /// „dokąd", a „jak" zostaje po stronie widoku. Zdarzenie zamiast właściwości, bo
    /// to jednorazowe polecenie, a nie stan — po przewinięciu ręką przez użytkownika
    /// właściwość kłamałaby o tym, gdzie siatka faktycznie stoi.
    /// </remarks>
    public event Action<double>? ScrollRequested;

    public async Task LoadAsync()
    {
        if (Anchor == default)
        {
            GoToToday();
        }

        await RefreshAsync();
        PrzewinDoTeraz();
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

        var wBazie = await calendar.StoredEventCountAsync();
        var naSiatce = dni.Sum(d => d.AllDay.Count + d.Timed.Count);

        Summary = $"W bazie {wBazie}, na tych dniach {naSiatce}.";

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
        PrzewinDoTeraz();
    }

    /// <summary>
    /// Trzy osobne polecenia zamiast jednego z liczbą.
    /// </summary>
    /// <remarks>
    /// Polecenie przyjmujące <c>int</c> dostawało z XAML-a **tekst**: zapis
    /// <c>CommandParameter="3"</c> to napis, a nie liczba, i nikt go po drodze nie
    /// zamienia. Polecenie ogólne odpowiada wtedy, że nie da się go wykonać, więc
    /// wszystkie trzy przyciski są wyszarzone — bez błędu, bez śladu i bez szansy,
    /// żeby zgadnąć przyczynę z wyglądu. Trzy polecenia bez parametru nie mają tego
    /// problemu w ogóle.
    /// </remarks>
    [RelayCommand]
    private Task ShowDay() => SetDaysAsync(1);

    [RelayCommand]
    private Task ShowThreeDays() => SetDaysAsync(3);

    [RelayCommand]
    private Task ShowWeek() => SetDaysAsync(7);

    private async Task SetDaysAsync(int days)
    {
        VisibleDays = days is 1 or 3 or 7 ? days : 3;

        // Tydzień zaczyna się w poniedziałek, a nie „od dziś przez siedem dni":
        // tydzień, który zaczyna się w środę, nie wygląda jak tydzień.
        if (VisibleDays == 7)
        {
            Anchor = Anchor.AddDays(-(((int)Anchor.DayOfWeek + 6) % 7));
        }
        else
        {
            // Powrót z tygodnia zostawiał zakotwiczenie na poniedziałku, więc „dzień"
            // po „tygodniu" pokazywał poniedziałek zamiast dzisiaj.
            GoToToday();
        }

        OnPropertyChanged(nameof(ColumnWidth));
        await RefreshAsync();
    }

    /// <summary>
    /// Siatka otwiera się na bieżącej godzinie, nie o północy.
    /// </summary>
    /// <remarks>
    /// Doba ma 1152 punkty wysokości, a ekran telefonu mieści z tego jakąś jedną
    /// czwartą — więc widok od północy pokazuje godziny, w których się śpi, i każde
    /// otwarcie kalendarza zaczyna się od przewijania. Godzina zapasu u góry, bo to,
    /// co się właśnie skończyło, jest częścią odpowiedzi na pytanie „co teraz".
    /// Przy dniach bez dzisiaj nie ma czego pokazywać: tam pozycja z poprzedniego
    /// przewinięcia niesie więcej niż godzina z innego dnia.
    /// </remarks>
    private void PrzewinDoTeraz()
    {
        var dzis = clock.Today;

        if (dzis < Anchor || dzis >= Anchor.AddDays(VisibleDays))
        {
            return;
        }

        var godzina = Math.Max(0, clock.Now.TimeOfDay.TotalHours - 1);

        ScrollRequested?.Invoke(godzina * HourHeight);
    }

    private void GoToToday()
    {
        Anchor = clock.Today;

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
            slot.Entry.Kind == AgendaKind.Task,
            slot.Entry.Color);
    }
}
