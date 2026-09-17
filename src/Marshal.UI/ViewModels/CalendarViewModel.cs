using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.UseCases;
using Marshal.Domain.Calendar;
using Marshal.Domain.Diagnostics;

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
    string Title,
    double Top,
    double Height,
    double Left,
    double Width,
    bool IsTask,
    string? Color,
    string StartText,
    string EndText,
    Guid? TaskId,
    string DayText,
    Guid? SourceId,
    string? ExternalId)
{
    /// <summary>Odhaczyć da się zadanie, nie cudze wydarzenie z kalendarza.</summary>
    /// <remarks>
    /// Odhaczenie wydarzenia Google znaczyłoby zapis do cudzego kalendarza, a zapis
    /// jest świadomie odłożony (spec 10.2): błąd w dwustronnej synchronizacji potrafi
    /// skasować wydarzenia w prawdziwym kalendarzu.
    /// </remarks>
    public bool CanComplete => IsTask && TaskId is not null;

    /// <summary>Godziny pokazujemy tylko wtedy, gdy blok ma je gdzie zmieścić.</summary>
    /// <remarks>
    /// Przy siedmiu dniach kolumna schodzi poniżej stu punktów i „10:00" zjadłoby
    /// cały tytuł. Kwadrans ma trzynaście punktów wysokości — dwie linijki godzin
    /// się tam nie mieszczą, więc też odpadają.
    /// </remarks>
    public bool ShowTimes => Width >= 120 && Height >= 34;

    /// <summary>Czy na bloku mieści się pole do odhaczenia.</summary>
    /// <remarks>
    /// Kwadrans ma trzynaście punktów wysokości, a pole razem z oprawą potrzebuje
    /// dwudziestu — na krótkim bloku wystawało poza jego krawędź i zasłaniało sąsiada.
    /// Krótkie zadanie odhacza się z listy albo po otwarciu szczegółu.
    /// </remarks>
    /// <remarks>
    /// Próg był ustawiony na dwadzieścia sześć punktów i przez to pole znikało
    /// z półgodzinnych zadań — czyli z większości. Zostaje tak długo, jak da się je
    /// wpisać w blok; poniżej dwunastu punktów już się nie da i wtedy odhacza się
    /// z listy albo z otwartego szczegółu.
    /// </remarks>
    public bool ShowCheck => CanComplete && Height >= 12 && Width >= 40;

    /// <summary>Rozmiar pola do odhaczenia — dopasowany do wysokości bloku.</summary>
    public double CheckSize => Math.Clamp(Height - 4, 10, 18);

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

/// <summary>Wpis na pasku całodniowym — z tożsamością, żeby dało się go otworzyć.</summary>
/// <remarks>
/// Do dziś pasek był jednym napisem sklejonym z tytułów. Zadanie na cały dzień nie
/// miało więc **żadnej** drogi do edycji: bloku na siatce nie ma, a napisu nie da się
/// kliknąć. Godzinę można było dopisać tylko przez listę „Następne".
/// </remarks>
public sealed record AllDayBox(string Title, Guid? TaskId, Guid? SourceId, string? ExternalId);

/// <summary>Jeden dzień siatki gotowy do narysowania.</summary>
public sealed record CalendarColumn(
    DateOnly Date,
    string Header,
    IReadOnlyList<AllDayBox> AllDay,
    IReadOnlyList<SlotBox> Slots,
    bool IsToday,
    double NowTop)
{
    public bool HasAllDay => AllDay.Count > 0;

    public string AllDayText => string.Join("  ·  ", AllDay.Select(w => w.Title));
}

/// <summary>
/// Kalendarz godzinowy: dzień, trzy dni, tydzień (spec 11).
/// </summary>
public sealed partial class CalendarViewModel(
    CalendarSyncService calendar, IClock clock, IActivityLog log, TaskEditService edit)
    : ObservableObject
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
    /// <summary>Tydzień jako widok domyślny — o to prosi układ tygodnia, nie trzy dni.</summary>
    public partial int VisibleDays { get; set; } = 7;

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

    /// <summary>Szerokość, jaką okno oddaje na kolumny. Zero, dopóki okno się nie zmierzy.</summary>
    /// <remarks>
    /// Podawana przez widok, bo tylko on ją zna. Model widoku nie pyta okna o rozmiar —
    /// dostaje go i przelicza, co z niego wynika.
    /// </remarks>
    private double _doDyspozycji;

    /// <summary>Najwęższa kolumna, jaką da się jeszcze czytać.</summary>
    private const double NajwezszaKolumna = 96;

    /// <summary>
    /// Szerokość kolumny dnia.
    /// </summary>
    /// <remarks>
    /// Liczona z tego, co okno faktycznie ma, a nie ze stałej na widok: przy trzech
    /// dniach na szerokim monitorze zostawało dwie trzecie pustego miejsca obok siatki,
    /// a w wąskim oknie trzeba było przewijać w bok, żeby zobaczyć trzeci dzień.
    /// Dolna granica jest po to, żeby tydzień w wąskim oknie dał się przewinąć w bok,
    /// zamiast zostać siedmioma nieczytelnymi paskami.
    /// </remarks>
    public double ColumnWidth => _doDyspozycji <= 0
        ? VisibleDays switch { 1 => 520, 3 => 240, _ => 130 }
        : Math.Max(NajwezszaKolumna, _doDyspozycji / VisibleDays);

    /// <summary>
    /// Nowa szerokość od okna. Przelicza siatkę, o ile zmiana cokolwiek znaczy.
    /// </summary>
    /// <remarks>
    /// Zdarzenie rozmiaru sypie się przy każdym ruchu ramki okna, a przeliczenie siatki
    /// to przebudowanie wszystkich bloków. Próg pół punktu odcina ruch, którego i tak
    /// nie widać, bo blok o pół punktu szerszy wygląda identycznie.
    /// </remarks>
    public void SetAvailableWidth(double width)
    {
        var stara = ColumnWidth;
        _doDyspozycji = Math.Max(0, width);

        if (Math.Abs(ColumnWidth - stara) < 0.5)
        {
            return;
        }

        OnPropertyChanged(nameof(ColumnWidth));
        Przelicz();
    }

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

    /// <summary>
    /// Kliknięty blok zadania. Szczegół należy do okna głównego, nie do kalendarza —
    /// to ta sama nakładka, która otwiera się z list, i ma zostać jedna.
    /// </summary>
    public event Action<Guid>? TaskRequested;

    /// <summary>Otwarcie wpisu z paska całodniowego — zadania albo wydarzenia.</summary>
    public void OpenAllDay(AllDayBox? wpis)
    {
        if (wpis is null)
        {
            return;
        }

        if (wpis.TaskId is { } identyfikator)
        {
            TaskRequested?.Invoke(identyfikator);
            return;
        }

        OpenTaskCommand.Execute(new SlotBox(
            wpis.Title, 0, 0, 0, 0, IsTask: false, Color: null,
            StartText: "—", EndText: "—", TaskId: null,
            DayText: Anchor.ToString("dd.MM.yyyy"), wpis.SourceId, wpis.ExternalId));
    }

    /// <summary>
    /// Przeciągnięcie zadania na inny dzień i godzinę.
    /// </summary>
    /// <remarks>
    /// Zmienia dokładnie dwie rzeczy — dzień i godzinę — i idzie osobną drogą niż zapis
    /// z okna szczegółu. Długość zostaje: przeciągnięcie przesuwa, a nie skraca.
    /// </remarks>
    public async Task MoveAsync(Guid taskId, DateOnly day, double punkty)
    {
        var pora = Pora(punkty);

        try
        {
            await edit.RescheduleAsync(taskId, day, pora);
            await log.RecordAsync(
                "Kalendarz: przełożenie", $"{day:yyyy-MM-dd} {pora:HH}:{pora:mm}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Kalendarz: przełożenie", "nie udało się", ActivityLevel.Problem, e.Message);
        }

        await RefreshAsync();
    }

    /// <summary>
    /// Przeciągnięcie wydarzenia z podłączonego kalendarza.
    /// </summary>
    /// <remarks>
    /// <para>
    /// To jedyne miejsce w aplikacji, w którym ruch ręki zmienia coś **poza** Marshalem:
    /// zapis idzie natychmiast do kalendarza Google, bez pytania. Stąd dwie rzeczy.
    /// </para>
    /// <para>
    /// Długość zostaje taka, jaką widać na bloku — przeciągnięcie przesuwa, nie skraca.
    /// </para>
    /// <para>
    /// Poprzednie godziny zostają zapamiętane i da się je przywrócić jednym kliknięciem.
    /// Przy zapisie do cudzego kalendarza „cofnij" nie jest wygodą, tylko jedyną
    /// odpowiedzią na omsknięcie ręki.
    /// </para>
    /// </remarks>
    public async Task MoveEventAsync(SlotBox blok, DateOnly day, double punkty)
    {
        ArgumentNullException.ThrowIfNull(blok);

        if (blok is not { SourceId: { } zrodlo, ExternalId: { } identyfikator })
        {
            return;
        }

        var strefa = clock.Now.Offset;
        var start = new DateTimeOffset(day.ToDateTime(Pora(punkty)), strefa);
        var dlugosc = TimeSpan.FromHours(Math.Max(0.25, blok.Height / HourHeight));

        try
        {
            var skad = Bylo(blok, strefa);

            await calendar.SaveEventAsync(
                zrodlo, identyfikator, new CalendarDraft(blok.Title, start, start + dlugosc));

            Undo = new MovedEvent(zrodlo, identyfikator, blok.Title, skad, skad + dlugosc);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(UndoText));

            await log.RecordAsync(
                "Kalendarz: przeniesienie wydarzenia",
                $"{blok.Title} na {day:yyyy-MM-dd} {start:HH}:{start:mm}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Kalendarz: przeniesienie wydarzenia", blok.Title,
                ActivityLevel.Problem, e.Message);
        }

        await RefreshAsync();
    }

    /// <summary>Skąd wydarzenie przyszło — z dnia i godziny widocznych na bloku.</summary>
    private static DateTimeOffset Bylo(SlotBox blok, TimeSpan strefa)
    {
        var dzien = DateOnly.ParseExact(blok.DayText, "dd.MM.yyyy", CultureInfo.InvariantCulture);
        var pora = TimeOnly.TryParse(blok.StartText, CultureInfo.InvariantCulture, out var g)
            ? g
            : TimeOnly.MinValue;

        return new DateTimeOffset(dzien.ToDateTime(pora), strefa);
    }

    /// <summary>Ostatnie przeniesienie wydarzenia — do cofnięcia.</summary>
    public sealed record MovedEvent(
        Guid SourceId, string ExternalId, string Title, DateTimeOffset Start, DateTimeOffset End);

    [ObservableProperty]
    public partial MovedEvent? Undo { get; set; }

    public bool CanUndo => Undo is not null;

    public string UndoText => Undo is { } ruch
        ? $"Przeniesiono „{ruch.Title}”. Cofnąć na {ruch.Start:dd.MM} {ruch.Start:HH}:{ruch.Start:mm}?"
        : string.Empty;

    [RelayCommand]
    private async Task UndoMoveAsync()
    {
        if (Undo is not { } ruch)
        {
            return;
        }

        try
        {
            await calendar.SaveEventAsync(
                ruch.SourceId, ruch.ExternalId,
                new CalendarDraft(ruch.Title, ruch.Start, ruch.End));

            await log.RecordAsync("Kalendarz: cofnięcie przeniesienia", ruch.Title);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));
        }

        ForgetUndo();
        await RefreshAsync();
    }

    [RelayCommand]
    private void ForgetUndo()
    {
        Undo = null;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoText));
    }


    /// <summary>Krok, do którego przyciąga się godzina przy przeciąganiu.</summary>
    /// <remarks>
    /// Pięć minut: kwadrans był za grubą miarką na spotkanie o 9:35, a minuta co do
    /// punktu byłaby udawaną precyzją — trafienie w 14:07 nie znaczy, że ktoś planuje
    /// na 14:07.
    /// </remarks>
    private const int Krok = 5;

    public static TimeOnly Pora(double punkty)
    {
        var minuty = Math.Clamp(punkty / HourHeight * 60, 0, (24 * 60) - Krok);
        var kroki = (int)(minuty / Krok) * Krok;

        return new TimeOnly(kroki / 60, kroki % 60);
    }

    /// <summary>Kliknięcie w pustą siatkę: nowe zadanie na tym dniu i o tej godzinie.</summary>
    public event Action<DateOnly, TimeOnly>? NewTaskRequested;

    /// <summary>
    /// Zgłoszenie z widoku: klik w puste miejsce kolumny, na wysokości <paramref name="punkty"/>.
    /// </summary>
    /// <remarks>
    /// Godzina zaokrąglana w dół do kwadransa. Minuta wzięta co do punktu byłaby
    /// udawaną precyzją: trafienie w 14:07 nie znaczy, że ktoś planuje na 14:07.
    /// </remarks>
    public void NewAt(DateOnly day, double punkty)
    {
        NewTaskRequested?.Invoke(day, Pora(punkty));
    }

    public async Task LoadAsync()
    {
        if (Anchor == default)
        {
            GoToToday();
        }

        await RefreshAsync();
        PrzewinDoTeraz();
    }

    /// <summary>
    /// Ostatnio złożona siatka. Trzymana, żeby zmiana szerokości okna nie musiała
    /// jechać do bazy — bloki zależą od szerokości, a dane nie.
    /// </summary>
    private IReadOnlyList<AgendaDay> _dni = [];

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _dni = await calendar.AgendaAsync(Anchor, VisibleDays);

        Przelicz();

        var wBazie = await calendar.StoredEventCountAsync();
        var naSiatce = _dni.Sum(d => d.AllDay.Count + d.Timed.Count);

        Summary = $"W bazie {wBazie}, na tych dniach {naSiatce}. "
            + $"Godziny w strefie {calendar.ZoneName}.";

        // Zła strefa przesuwa wszystko naraz i wygląda przez to jak źle pobrane dane.
        // Nie nadpisujemy kłopotu z pobierania — ten jest świeższy i bardziej konkretny.
        if (Problem is null && calendar.ZoneProblem is { } klopot)
        {
            Problem = klopot;
            OnPropertyChanged(nameof(HasProblem));
        }

        // Zapisujemy tylko przypadek podejrzany: wydarzenia są, a siatka pusta.
        // Wpis przy każdym przerysowaniu zalałby dziennik tym, co widać na ekranie,
        // i utopiłby w tym rzeczy, których nie widać nigdzie indziej.
        if (naSiatce == 0 && wBazie > 0)
        {
            await log.RecordAsync(
                "Kalendarz: siatka",
                $"w bazie {wBazie}, na siatce 0",
                ActivityLevel.Problem,
                $"Zakres {Range}. Wydarzenia są, ale żadne nie wypada na pokazywanych dniach.");
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

        await log.RecordAsync(
            "Kalendarz: pobranie",
            $"odświeżonych {raport.Sources}, wydarzeń {raport.Events}, nieudanych {raport.Failed}",
            raport.Failed > 0 ? ActivityLevel.Problem : ActivityLevel.Ok,
            string.Join(Environment.NewLine, raport.Problems.Distinct()));

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

    /// <summary>Złożenie kolumn z ostatnio pobranych dni. Bez sieci i bez bazy.</summary>
    private void Przelicz()
    {
        var dzis = clock.Today;
        var teraz = clock.Now.TimeOfDay.TotalHours * HourHeight;

        Columns.Clear();
        foreach (var dzien in _dni)
        {
            Columns.Add(new CalendarColumn(
                dzien.Date,
                $"{DayNames[((int)dzien.Date.DayOfWeek + 6) % 7]} {dzien.Date.Day}",
                dzien.AllDay.Select(e => new AllDayBox(
                    e.Title, e.TaskId, e.SourceId, e.ExternalId)).ToList(),
                dzien.Timed.Select(Box).ToList(),
                dzien.Date == dzis,
                teraz));
        }
    }

    /// <summary>
    /// Odhaczenie zadania wprost z siatki.
    /// </summary>
    /// <remarks>
    /// Zadanie z rytmem rodzi przy odhaczeniu następne wystąpienie, więc siatka musi
    /// się przeliczyć z bazy, a nie tylko wyrzucić odhaczony blok: następnik potrafi
    /// wypaść na tym samym widocznym dniu.
    /// </remarks>
    [RelayCommand]
    private async Task CompleteAsync(Guid? id)
    {
        if (id is not { } identyfikator)
        {
            return;
        }

        await edit.CompleteAsync(identyfikator);
        await RefreshAsync();
    }

    /// <summary>Otwarcie szczegółu zadania z siatki. Wydarzenia Google nie mają czego otwierać.</summary>
    /// <summary>Otwarty blok: zadanie idzie do nakładki szczegółu, wydarzenie na kartę obok.</summary>
    /// <remarks>
    /// Wydarzenia Google nie da się tu zmienić — zapis do cudzego kalendarza jest
    /// świadomie odłożony (spec 10.2). Ale „nie da się zmienić" to nie to samo co
    /// „kliknięcie nic nie robi": drugie wygląda jak zepsuty przycisk. Karta mówi,
    /// co to jest i skąd pochodzi.
    /// </remarks>
    [RelayCommand]
    private void OpenTask(SlotBox? blok)
    {
        if (blok is null)
        {
            return;
        }

        if (blok.TaskId is { } identyfikator)
        {
            TaskRequested?.Invoke(identyfikator);
            return;
        }

        Opened = blok;

        OpenedTitle = blok.Title;
        OpenedStart = Pora(blok.StartText);
        OpenedEnd = Pora(blok.EndText);
        OpenedProblem = null;

        // Przycisków zapisu nie pokazujemy tam, gdzie zapis i tak nie ma dokąd pójść.
        CanEditOpened = blok.SourceId is not null
            && blok.ExternalId is not null
            && calendar.CanWrite(CalendarKind.Google);

        OnPropertyChanged(nameof(HasOpenedProblem));
    }

    private static TimeSpan? Pora(string tekst) =>
        TimeSpan.TryParse(tekst, CultureInfo.InvariantCulture, out var pora) ? pora : null;

    [ObservableProperty]
    public partial string OpenedTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TimeSpan? OpenedStart { get; set; }

    [ObservableProperty]
    public partial TimeSpan? OpenedEnd { get; set; }

    [ObservableProperty]
    public partial bool CanEditOpened { get; set; }

    [ObservableProperty]
    public partial string? OpenedProblem { get; set; }

    public bool HasOpenedProblem => !string.IsNullOrEmpty(OpenedProblem);

    /// <summary>
    /// Zapis zmienionego wydarzenia do kalendarza, z którego pochodzi.
    /// </summary>
    /// <remarks>
    /// Nieudany zapis **zostawia kartę otwartą**. Zamknięcie jej wyglądałoby identycznie
    /// jak zapis udany, a przy pisaniu do cudzego kalendarza to jest różnica między
    /// „zmienione" a „wydaje ci się, że zmienione".
    /// </remarks>
    [RelayCommand]
    private async Task SaveOpenedAsync()
    {
        if (Opened is not { SourceId: { } zrodlo, ExternalId: { } identyfikator } blok)
        {
            return;
        }

        if (OpenedStart is not { } od || OpenedEnd is not { } doGodziny)
        {
            OpenedProblem = "Bez godzin nie ma czego zapisać.";
            OnPropertyChanged(nameof(HasOpenedProblem));
            return;
        }

        try
        {
            var dzien = DateOnly.ParseExact(blok.DayText, "dd.MM.yyyy", CultureInfo.InvariantCulture);
            var strefa = clock.Now.Offset;

            var start = new DateTimeOffset(dzien.ToDateTime(TimeOnly.FromTimeSpan(od)), strefa);
            var koniec = doGodziny > od
                ? new DateTimeOffset(dzien.ToDateTime(TimeOnly.FromTimeSpan(doGodziny)), strefa)
                : start.AddMinutes(30);

            await calendar.SaveEventAsync(
                zrodlo, identyfikator, new CalendarDraft(OpenedTitle, start, koniec));

            await log.RecordAsync("Kalendarz: zapis wydarzenia", OpenedTitle);

            Opened = null;
            await RefreshAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            OpenedProblem = e.Message;
            OnPropertyChanged(nameof(HasOpenedProblem));

            await log.RecordAsync(
                "Kalendarz: zapis wydarzenia", OpenedTitle, ActivityLevel.Problem, e.Message);
        }
    }

    /// <summary>Skasowanie wydarzenia u źródła. Nie wraca, więc pyta o potwierdzenie.</summary>
    [RelayCommand]
    private async Task DeleteOpenedAsync()
    {
        if (Opened is not { SourceId: { } zrodlo, ExternalId: { } identyfikator })
        {
            return;
        }

        if (!ConfirmDelete)
        {
            OpenedProblem = "Skasowanego wydarzenia nie da się odzyskać. "
                + "Zaznacz potwierdzenie, jeśli na pewno.";
            OnPropertyChanged(nameof(HasOpenedProblem));
            return;
        }

        try
        {
            await calendar.DeleteEventAsync(zrodlo, identyfikator);
            await log.RecordAsync("Kalendarz: skasowanie wydarzenia", OpenedTitle);

            Opened = null;
            ConfirmDelete = false;
            await RefreshAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            OpenedProblem = e.Message;
            OnPropertyChanged(nameof(HasOpenedProblem));

            await log.RecordAsync(
                "Kalendarz: skasowanie wydarzenia", OpenedTitle, ActivityLevel.Problem, e.Message);
        }
    }

    /// <summary>Świadome potwierdzenie kasowania. Gaśnie razem z kartą.</summary>
    [ObservableProperty]
    public partial bool ConfirmDelete { get; set; }

    /// <summary>Otwarte wydarzenie. Puste, gdy karta jest zamknięta.</summary>
    [ObservableProperty]
    public partial SlotBox? Opened { get; set; }

    public bool HasOpened => Opened is not null;

    partial void OnOpenedChanged(SlotBox? value) => OnPropertyChanged(nameof(HasOpened));

    [RelayCommand]
    private void CloseOpened()
    {
        Opened = null;
        ConfirmDelete = false;
        OpenedProblem = null;
        OnPropertyChanged(nameof(HasOpenedProblem));
    }

    /// <summary>
    /// Przesunięcie kreski bieżącej godziny. Woła je okno co minutę.
    /// </summary>
    /// <remarks>
    /// Kreska liczona była wyłącznie przy składaniu siatki, więc stała tam, gdzie
    /// wypadła przy otwarciu ekranu — po pięciu godzinach z otwartą aplikacją
    /// pokazywała godzinę sprzed pięciu godzin i wyglądała jak błąd w strefie czasowej.
    /// </remarks>
    public void Tick()
    {
        if (Columns.Any(k => k.IsToday))
        {
            Przelicz();
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
            slot.Entry.Color,

            // Godziny z wpisu, nie z pozycji na siatce: wpis przycięty do dnia ma
            // północ na krawędzi, a pokazać trzeba to, co jest umówione.
            slot.Entry.Start.ToString("HH:mm"),
            slot.Entry.End.ToString("HH:mm"),
            slot.Entry.TaskId,
            slot.Entry.Start.ToString("dd.MM.yyyy"),
            slot.Entry.SourceId,
            slot.Entry.ExternalId);
    }
}
