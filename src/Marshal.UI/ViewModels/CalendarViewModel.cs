using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Application.Repositories;
using Marshal.Domain.Calendar;
using Marshal.Domain.Contacts;
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
    string? ExternalId,
    bool IsDone,

    /// <summary>Czy do kalendarza, z którego pochodzi wpis, da się pisać.</summary>
    bool CanWrite = false)
{
    /// <summary>
    /// Co da się odhaczyć: własne zadanie i wydarzenie z kalendarza, do którego umiemy pisać.
    /// </summary>
    /// <remarks>
    /// Wydarzenie nie ma u nas pola „zrobione" — ptaszek idzie do jego nazwy w Google,
    /// więc bez prawa zapisu odhaczenie nie miałoby gdzie wylądować. Kanał iCal jest
    /// tylko do odczytu i pole wyboru się tam nie pokazuje: przycisk, który nic nie
    /// robi, jest gorszy od jego braku.
    /// </remarks>
    public bool CanComplete =>
        IsTask ? TaskId is not null : CanWrite && SourceId is not null && ExternalId is not null;

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
    /// <remarks>
    /// Szerokość podniesiona z czterdziestu punktów do pięćdziesięciu sześciu, odkąd
    /// tydzień mieści się na telefonie. Przy kolumnie czterdziestopunktowej kwadracik
    /// zabierał połowę bloku i na tytuł zostawały dwa znaki — czyli blok przestawał
    /// mówić, czego dotyczy, żeby dało się go odhaczyć. Odwrotnie niż powinno:
    /// odhaczyć da się z listy i z otwartego szczegółu, a przeczytać nie da się nigdzie
    /// indziej.
    /// </remarks>
    /// <remarks>
    /// <b>Nie na dotyku.</b> Wszystkie progi wyżej mówią o tym, czy kwadracik zmieści
    /// się w bloku — a na telefonie pytanie brzmi inaczej: czy da się w niego trafić.
    /// Nie da się. Blok kwadransa ma kilkanaście punktów wysokości, więc kwadracik
    /// wychodzi mniejszy od opuszka i leży na czymś, co równocześnie przeciąga się
    /// i otwiera; trafienie obok przesuwa wydarzenie, trafienie w środek odhacza coś,
    /// na co się tylko patrzyło. Na pulpicie kwadracik odsłania się przy najechaniu
    /// i trafia się w niego kursorem co do punktu — tam zostaje.
    ///
    /// Ptaszek odhaczonego wpisu zostaje wszędzie: to jest stan, nie przycisk.
    /// </remarks>
    public bool ShowCheck =>
        !Platform.Touch && CanComplete && Height >= 12 && Width >= 56;

    /// <summary>
    /// Czy zostawić z lewej miejsce na znacznik.
    /// </summary>
    /// <remarks>
    /// Odhaczone zadanie pokazuje ptaszek **zawsze**, także na bloku zbyt niskim na
    /// pole wyboru: ptaszek jest samym napisem i mieści się tam, gdzie kontrolka już
    /// nie. Inaczej najkrótsze zadania traciły jedyny ślad tego, że są zrobione.
    /// </remarks>
    public bool ShowMarkColumn => ShowCheck || IsDone;

    /// <summary>
    /// Rozmiar pola do odhaczenia — dopasowany do wysokości bloku.
    /// </summary>
    /// <remarks>
    /// Kwadracik rysujemy sami, z ramki i napisu, zamiast używać gotowego pola wyboru.
    /// Gotowe ma w motywie własną najmniejszą wysokość i własne odstępy, których nie
    /// da się zejść poniżej: pomniejszanie go skalą kończyło się kontrolką ułożoną
    /// na trzydzieści dwa punkty, narysowaną na szesnaście i przyciętą krawędzią
    /// bloku do rogu. Trzy rundy poprawek na coś, co z dwóch prostych elementów
    /// wychodzi od razu.
    /// </remarks>
    public double CheckSize => Math.Clamp(Height - 6, 10, 16);

    /// <summary>
    /// Pomniejszenie pola wyboru do rozmiaru bloku.
    /// </summary>
    /// <remarks>
    /// Sama szerokość nie wystarcza: kwadrat pola jest w motywie wpisany na stałe,
    /// więc pole ustawione na dwanaście punktów i tak rysowało się na dwadzieścia
    /// i wychodziło poza bloczek. Skala zmienia to, co widać, a nie tylko miejsce,
    /// które pole dostaje.
    /// </remarks>
    /// <summary>Wielkość samego ptaszka w kwadraciku.</summary>
    public double MarkSize => Math.Max(8, CheckSize - 3);

    /// <summary>Zadanie półprzezroczyste: umowa z kimś i zamiar wobec siebie to nie to samo.</summary>
    public double Opacity => IsTask ? 0.55 : 1.0;

    /// <summary>Barwa kalendarza albo zadania, przyciemniona przezroczystością.</summary>
    public IBrush Background => Palette.Background(Color, IsTask, Palette.OnGrid);
}

/// <summary>
/// Tło wpisu z barwy obszaru — jedno liczenie na wszystkie widoki.
/// </summary>
/// <remarks>
/// <para>
/// Barwy z Google to jasne pastele, a okno bywa ciemne — położone wprost dawałyby jasny
/// prostokąt z jasnym napisem. Ta sama barwa z przezroczystością zachowuje odcień,
/// po którym poznaje się obszar, i zostawia tekst czytelnym w obu motywach.
/// </para>
/// <para>
/// Wspólne dla siatki godzinowej i miesiąca, bo <b>ten sam obszar musi mieć ten sam
/// odcień w obu</b>. Dwa liczenia rozjeżdżają się przy pierwszej poprawce w jednym
/// z nich, a rozjechany odcień psuje dokładnie to, do czego barwa tu służy: rozpoznanie
/// obszaru bez czytania.
/// </para>
/// </remarks>
internal static class Palette
{
    /// <summary>Barwa dla wpisu bez własnej. Zadanie inne niż wydarzenie, żeby dało się je odróżnić.</summary>
    private const string DefaultEvent = "#6C8FBF";

    private const string DefaultTask = "#6E78A0";

    /// <summary>Krycie bloku na siatce godzinowej.</summary>
    public const byte OnGrid = 0x66;

    /// <summary>
    /// Krycie paska w komórce miesiąca — słabsze.
    /// </summary>
    /// <remarks>
    /// Blok na siatce dnia jest wysoki i stoi w rzadkim otoczeniu; pasek w miesiącu ma
    /// jedenaście punktów wysokości i sąsiaduje z trzema innymi w komórce szerokiej na
    /// jedną siódmą okna. To samo krycie robi z tygodnia pasiastą ścianę, w której nie
    /// widać ani nazw, ani numerów dni. Odcień zostaje ten sam, co na siatce — rozpoznaje
    /// się go po barwie, nie po jej sile.
    /// </remarks>
    public const byte InCell = 0x38;

    public static IBrush Background(string? color, bool task, byte opacity)
    {
        var fallback = task ? DefaultTask : DefaultEvent;

        var source = string.IsNullOrWhiteSpace(color) ? fallback : color;

        // Barwa nie do odczytania wraca do domyślnej **z tym samym kryciem**. Wcześniej
        // wracała krycie pełne i jeden nieudany odczyt dawał jedyny nieprzezroczysty
        // prostokąt na siatce — czyli wpis wyróżniony za to, że coś z nim nie tak.
        var parsed = Color.TryParse(source, out var read)
            ? read
            : Color.Parse(fallback);

        return new SolidColorBrush(Color.FromArgb(opacity, parsed.R, parsed.G, parsed.B));
    }
}

/// <summary>Wpis na pasku całodniowym — z tożsamością, żeby dało się go otworzyć.</summary>
/// <remarks>
/// Do dziś pasek był jednym napisem sklejonym z tytułów. Zadanie na cały dzień nie
/// miało więc **żadnej** drogi do edycji: bloku na siatce nie ma, a napisu nie da się
/// kliknąć. Godzinę można było dopisać tylko przez listę „Następne".
/// </remarks>
public sealed record AllDayBox(
    string Title, Guid? TaskId, Guid? SourceId, string? ExternalId, bool IsDone = false,

    /// <summary>Czy do kalendarza, z którego wpis pochodzi, da się pisać.</summary>
    bool CanWrite = false)
{
    /// <summary>
    /// Napis na pasku — z ptaszkiem, gdy wpis jest odhaczony.
    /// </summary>
    /// <remarks>
    /// Na pasku całodniowym nie ma miejsca na pole wyboru, a znak zdejmowany jest
    /// z nazwy przy rysowaniu. Bez dopisania go tutaj odhaczone wydarzenie całodniowe
    /// wyglądałoby na pasku tak samo jak nieodhaczone — czyli ptaszek postawiony
    /// w Google przestawałby być widoczny u nas.
    /// </remarks>
    public string Label => IsDone ? $"✓ {Title}" : Title;
}

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


}

/// <summary>Jeden wpis w komórce miesiąca — jedna linijka, bo tyle się mieści.</summary>
/// <remarks>
/// Godzina osobno od nazwy, żeby dało się ją wyrównać i przygasić. Sklejona z nazwą
/// w jeden napis zlewałaby się z nią przy trzydziestu komórkach naraz.
/// </remarks>
public sealed record MonthEntry(
    string Time, string Title, Guid? TaskId, bool IsDone, string? Color)
{
    public string Label => IsDone ? $"✓ {Title}" : Title;

    public bool HasTime => Time.Length > 0;

    /// <summary>
    /// Tło w barwie obszaru — to samo, po którym rozpoznaje się wpis na siatce dnia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Miesiąc jest widokiem, w którym nie czyta się nazw, tylko patrzy, gdzie jest
    /// gęsto. Same napisy odpowiadają na to najgorzej: cztery jednakowe linijki w każdej
    /// komórce wyglądają tak samo niezależnie od tego, czy to cztery rzeczy z pracy,
    /// czy trzy przedszkolne i jedna urzędowa. Barwa obszaru odpowiada na to bez
    /// czytania — a jest już policzona, bo siatka dnia rysuje nią bloki.
    /// </para>
    /// <para>
    /// Zadanie nie dostaje tu przygaszenia, które ma na siatce. Tam przygaszony jest
    /// cały blok wysoki na kilkadziesiąt punktów; tutaj przygaszenie zdjęłoby połowę
    /// czytelności z napisu o wysokości jedenastu, a odróżnienie zadania od wydarzenia
    /// niesie już domyślna barwa i brak godziny.
    /// </para>
    /// </remarks>
    public IBrush Background => Palette.Background(Color, TaskId is not null, Palette.InCell);
}

/// <summary>Jedna komórka siatki miesiąca.</summary>
/// <remarks>
/// <para>
/// <b>Nadmiar liczony, nie chowany po cichu.</b> Komórka mieści kilka linijek, a dzień
/// bywa gęstszy. Ucięta lista bez śladu znaczyłaby, że miesiąc pokazuje mniej, niż jest,
/// i nie mówi o tym — czyli najgorszy rodzaj widoku ogólnego.
/// </para>
/// <para>
/// Dni spoza miesiąca zostają na siatce, tylko przygaszone. Tydzień na przełomie jest
/// tygodniem i wycięcie z niego trzech dni robi dziurę tam, gdzie jej nie ma.
/// </para>
/// </remarks>
public sealed record MonthCell(
    DateOnly Date,
    string Day,
    bool IsToday,
    bool IsOtherMonth,
    IReadOnlyList<MonthEntry> Entries,
    int Overflow)
{
    public bool HasOverflow => Overflow > 0;

    /// <summary>Licznik nadmiaru przy numerze dnia, nie pod listą.</summary>
    /// <remarks>
    /// Pod listą wyglądał lepiej i znikał pierwszy: komórka dostaje tyle wysokości, ile
    /// zostanie po podziale okna między tygodnie, a przy sześciu tygodniach na telefonie
    /// bywa jej mniej niż treści. Przycinane jest to, co na dole — czyli akurat jedyna
    /// wiadomość o tym, że coś jest przycięte. Przy numerze dnia zostaje zawsze.
    /// </remarks>
    public string OverflowLabel => $"+{Overflow}";
}

/// <summary>Jeden tydzień siatki miesiąca — siedem komórek.</summary>
public sealed record MonthWeek(IReadOnlyList<MonthCell> Cells);

/// <summary>Jedna pozycja w wyborze zakresu.</summary>
/// <remarks>
/// Zakres jest <b>jedną decyzją o czterech możliwościach</b>, a nie czterema
/// przyciskami. Przy czterech osobnych trzeba je najpierw obejrzeć, żeby zobaczyć,
/// który jest wciśnięty; przy jednym polu odpowiedź stoi napisana w środku.
/// Na szerokim oknie cztery równe pola rozciągały się zresztą przez cały ekran
/// i wyglądały jak pasek narzędzi, a nie jak wybór.
/// </remarks>
public sealed record ViewRange(string Name, int Days, bool Month)
{
    /// <summary>Pole wyboru pokazuje to, co zwraca ta metoda — stąd własna, nie ta z rekordu.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Kalendarz: dzień, trzy dni, tydzień, miesiąc (spec 11).
/// </summary>
public sealed partial class CalendarViewModel(
    CalendarSyncService calendar,
    IClock clock,
    IActivityLog log,
    TaskEditService edit,
    IContactRepository? people = null,
    IUnitOfWork? work = null,
    IHlcSource? logicalClock = null)
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

    /// <summary>
    /// Czy oglądamy miesiąc.
    /// </summary>
    /// <remarks>
    /// Osobna właściwość, a nie kolejna wartość <see cref="VisibleDays"/>. Miesiąc nie
    /// jest „siatką godzinową o innej liczbie dni": nie ma w nim godzin, kolumn ani
    /// paska całodniowego, a ma numery dni i nadmiar. Wciśnięty w tamten model
    /// zmusiłby połowę obliczeń siatki do sprawdzania, czy przypadkiem nie jest
    /// miesiącem — a to jest ten rodzaj warunku, który potem zostaje wszędzie.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsMonth { get; set; }

    /// <summary>Możliwe zakresy, w kolejności od najwęższego.</summary>
    public IReadOnlyList<ViewRange> Ranges { get; } =
    [
        new("dzień", 1, false),
        new("3 dni", 3, false),
        new("tydzień", 7, false),
        new("miesiąc", 0, true),
    ];

    /// <summary>
    /// Wybrany zakres. Ustawiany także z zewnątrz — stąd zapora przed odbiciem.
    /// </summary>
    /// <remarks>
    /// Zakres zmienia się nie tylko z pola wyboru: dotknięcie dnia w miesiącu schodzi
    /// na siatkę godzinową, a powrót z miesiąca wraca na dzisiaj. Gdyby każde takie
    /// przestawienie wracało tu jako wybór użytkownika, siatka przeliczałaby się dwa
    /// razy, a przy zejściu z miesiąca — z niewłaściwym zakotwiczeniem.
    /// </remarks>
    [ObservableProperty]
    public partial ViewRange? SelectedRange { get; set; }

    private bool _ownSetting;

    partial void OnSelectedRangeChanged(ViewRange? value)
    {
        if (_ownSetting || value is null)
        {
            return;
        }

        _ = Choose(value);
    }

    private async Task Choose(ViewRange scope)
    {
        try
        {
            await (scope.Month ? ShowMonthCommand.ExecuteAsync(null) : SetDaysAsync(scope.Days));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Zmiana zakresu idzie z pola wyboru, a nie z polecenia, więc wyjątek nie
            // ma dokąd trafić: pole po prostu pokazywałoby nowy zakres nad starą siatką.
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Kalendarz: zmiana zakresu", scope.Name, ActivityLevel.Problem, e.Message);
        }
    }

    /// <summary>Dociągnięcie pola wyboru do stanu, który ustawiono gdzie indziej.</summary>
    private void RememberRange()
    {
        _ownSetting = true;

        try
        {
            SelectedRange = IsMonth
                ? Ranges[^1]
                : Ranges.FirstOrDefault(z => !z.Month && z.Days == VisibleDays) ?? Ranges[2];
        }
        finally
        {
            _ownSetting = false;
        }
    }

    /// <summary>Tygodnie siatki miesiąca. Puste poza trybem miesiąca.</summary>
    public ObservableCollection<MonthWeek> MonthWeeks { get; } = [];

    /// <summary>Nazwy dni nad siatką miesiąca.</summary>
    public IReadOnlyList<string> MonthHeaders { get; } = DayNames;

    /// <summary>
    /// Czy okno jest wąskie — czyli czy patrzymy na to przez telefon.
    /// </summary>
    /// <remarks>
    /// Liczone z szerokości oddanej na kolumny, a nie brane z modelu głównego. Ta sama
    /// liczba rozstrzyga już o szerokości kolumn, więc kalendarz nie musi pytać nikogo
    /// o coś, co i tak wie; a próg jest ten sam, co przy pasku nawigacji, żeby ekran
    /// nie zmieniał się w dwóch miejscach przy dwóch różnych szerokościach.
    ///
    /// Zero znaczy „okno się jeszcze nie zmierzyło" i jest traktowane jak szerokie:
    /// przy pierwszym rysowaniu lepiej pokazać za dużo niż schować coś na stałe.
    /// </remarks>
    private bool Narrow => _available > 0 && _available < 720;

    /// <summary>
    /// Czy pokazywać linijkę „w bazie tyle, na tych dniach tyle".
    /// </summary>
    /// <remarks>
    /// Na telefonie nie. Jest to linijka do diagnozy — rozdziela trzy przyczyny pustej
    /// siatki, które wyglądają tak samo — a na wąskim ekranie zajmuje dwa wiersze nad
    /// kalendarzem i odpowiada na pytanie, którego się przy telefonie nie zadaje.
    /// Na komputerze zostaje, bo tam wysokość nie jest towarem deficytowym i bo to
    /// tam się siada, gdy coś naprawdę nie gra.
    /// </remarks>
    public bool ShowSummary => !Narrow;

    /// <summary>
    /// Czy zakres wybiera się przyciskami, czy polem wyboru.
    /// </summary>
    /// <remarks>
    /// Cztery przyciski mówią od razu, jakie są możliwości i który zakres jest teraz —
    /// jedno spojrzenie zamiast rozwijania. Na to trzeba jednak miejsca, a na telefonie
    /// stoją już w tej linii strzałki i pobieranie. Pole wyboru mówi to samo jednym
    /// słowem i mieści się tam, gdzie one nie. To nie są dwa wyglądy tej samej rzeczy,
    /// tylko dwie odpowiedzi na to, ile jest miejsca: szerokie okno stać na pokazanie
    /// wszystkiego naraz, wąskie nie.
    /// </remarks>
    public bool ShowRangeButtons => !Narrow;

    /// <summary>Pole wyboru zakresu — na wąskim, gdzie przyciski się nie mieszczą.</summary>
    public bool ShowRangePicker => Narrow;

    /// <summary>
    /// Ile wpisów mieści komórka, gdy nie wiadomo jeszcze, jak wysoka jest siatka.
    /// </summary>
    /// <remarks>
    /// Zero wysokości znaczy „okno się jeszcze nie zmierzyło", a nie „nie ma miejsca".
    /// Cztery to tyle, ile komórka mieściła, zanim liczyliśmy to z wysokości — czyli
    /// najgorszy przypadek jest równy temu, co było, a nie gorszy od niego.
    /// </remarks>
    private const int MonthFallback = 4;

    /// <summary>Margines, ramka i wyściółka komórki razem. Odpowiednik stylu w XAML-u.</summary>
    /// <remarks>
    /// Marginesy po 1, ramka po 1, wyściółka po 4 — z góry i z dołu, więc dwanaście.
    /// Liczone tu, a nie mierzone w oknie: mierzenie wymagałoby chodzenia po drzewie
    /// kontrolek przy każdym układzie, a te liczby stoją w stylu obok i zmieniają się
    /// razem z nim.
    /// </remarks>
    private const double CellBorder = 12;

    /// <summary>Wiersz z numerem dnia i licznikiem nadmiaru.</summary>
    private const double DayNumber = 20;

    /// <summary>
    /// Wysokość jednego wpisu w komórce.
    /// </summary>
    /// <remarks>
    /// Napis jedenastopunktowy z odstępem po punkcie z góry i z dołu wychodzi poniżej
    /// siedemnastu. Zaokrąglone <b>w górę</b> i to jest cała ostrożność tej liczby:
    /// przeszacowanie kosztuje czasem jedną linijkę miejsca, niedoszacowanie przycina
    /// wpis, którego licznik nadmiaru już nie policzył — czyli komórka chowa coś
    /// i o tym nie mówi. Z tych dwóch pomyłek tylko jedna jest cicha.
    /// </remarks>
    private const double EntryHeight = 18;

    /// <summary>Wysokość, jaką okno oddaje na tygodnie miesiąca. Zero, dopóki nie zmierzy.</summary>
    private double _weekHeight;

    /// <summary>
    /// Wysokość siatki tygodni — stąd wiadomo, ile wpisów mieści komórka.
    /// </summary>
    /// <remarks>
    /// Podawana przez widok, tak samo i z tego samego powodu co szerokość: model widoku
    /// nie pyta okna o rozmiar, dostaje go i przelicza, co z niego wynika.
    /// </remarks>
    public void SetMonthHeight(double height)
    {
        var fresh = Math.Max(0, height);

        if (Math.Abs(fresh - _weekHeight) < 1)
        {
            return;
        }

        _weekHeight = fresh;

        if (IsMonth)
        {
            RecomputeMonth();
        }
    }

    /// <summary>
    /// Ile wpisów mieści komórka przy tylu tygodniach na siatce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stała czwórka była tu podwójnie nietrafiona. Na komputerze komórka ma wysokość
    /// na sześć czy siedem linijek i chowała za licznikiem rzeczy, na które było
    /// miejsce. Na telefonie przy sześciu tygodniach bywa odwrotnie: czwarta linijka
    /// już się nie mieści, a komórka jest przycięta — więc licznik mówił „+1", gdy
    /// niewidoczne były dwie. Ta druga pomyłka jest gorsza, bo cicha.
    /// </para>
    /// <para>
    /// Co najmniej jeden wpis zawsze, nawet gdy nie mieści się i on. Komórka, w której
    /// nie widać niczego poza liczbą, nie mówi już nic o tym, czym dzień jest zajęty.
    /// </para>
    /// </remarks>
    private int FitsInCell(int weeks)
    {
        if (_weekHeight <= 0 || weeks <= 0)
        {
            return MonthFallback;
        }

        var forEntries = (_weekHeight / weeks) - CellBorder - DayNumber;

        return Math.Clamp((int)Math.Floor(forEntries / EntryHeight), 1, 20);
    }

    public double GridHeight => 24 * HourHeight;

    /// <summary>Szerokość, jaką okno oddaje na kolumny. Zero, dopóki okno się nie zmierzy.</summary>
    /// <remarks>
    /// Podawana przez widok, bo tylko on ją zna. Model widoku nie pyta okna o rozmiar —
    /// dostaje go i przelicza, co z niego wynika.
    /// </remarks>
    private double _available;

    /// <summary>
    /// Najwęższa kolumna, poniżej której tydzień zaczyna się przewijać w bok.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Było tu dziewięćdziesiąt sześć punktów — czyli tyle, ile trzeba, żeby w bloku
    /// zmieścił się tytuł w jednej linijce. Skutek: na telefonie tydzień nie mieścił
    /// się na ekranie i trzeba było przewijać w bok, żeby zobaczyć sobotę. Tydzień,
    /// którego nie widać naraz, nie odpowiada na pytanie, po które się go otwiera.
    /// </para>
    /// <para>
    /// Trzydzieści cztery punkty to szerokość, przy której tytuł jeszcze się zawija
    /// na kilka linijek zamiast zniknąć. Siedem takich kolumn mieści się na telefonie
    /// w pionie z zapasem. Poniżej tej granicy przewijanie wraca — bo sześć kolumn
    /// widocznych i jedna ucięta jest lepsze od siedmiu pasków bez treści.
    /// </para>
    /// </remarks>
    private const double NarrowestColumn = 34;

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
    public double ColumnWidth => _available <= 0
        ? VisibleDays switch { 1 => 520, 3 => 240, _ => 130 }

        // Minus odstęp między kolumnami. Bez tego siedem kolumn zajmowało czternaście
        // punktów więcej, niż okno miało — i pojawiał się poziomy pasek przewijania
        // na rzecz, która o włos się nie mieści.
        : Math.Max(NarrowestColumn, (_available / VisibleDays) - ColumnGap);

    /// <summary>Odstęp między kolumnami dnia. Musi zgadzać się z marginesem w oknie.</summary>
    private const double ColumnGap = 2;

    /// <summary>Wysokość jednego wiersza na pasku całodniowym.</summary>
    private const double AllDayRow = 24;

    /// <summary>
    /// Wysokość paska całodniowego — **wspólna dla wszystkich kolumn**.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Po jednej rzeczy na wiersz, a nie wszystkie obok siebie. Dwie rzeczy w jednym
    /// wierszu zostawiały z każdej po pięć znaków i kropki, więc pasek mówił, że coś
    /// jest, ale nie co.
    /// </para>
    /// <para>
    /// Wspólna dla wszystkich kolumn, bo pasek jest jednym pasmem przez cały tydzień,
    /// a nie siedmioma osobnymi. Przy wysokości liczonej osobno dzień z trzema
    /// rzeczami byłby wyższy od sąsiada z jedną i dolna krawędź pasma szłaby schodami.
    /// Dzień z jedną rzeczą ma więc puste miejsce pod nią.
    /// </para>
    /// </remarks>
    public double AllDayHeight => Math.Max(1, Columns.Count == 0 ? 0 : Columns.Max(k => k.AllDay.Count))
        * AllDayRow;

    /// <summary>Czy którykolwiek z widocznych dni ma coś całodniowego.</summary>
    public bool HasAnyAllDay => Columns.Any(k => k.AllDay.Count > 0);

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
        var old = ColumnWidth;
        _available = Math.Max(0, width);

        if (Math.Abs(ColumnWidth - old) < 0.5)
        {
            return;
        }

        OnPropertyChanged(nameof(ColumnWidth));
        OnPropertyChanged(nameof(ShowSummary));
        OnPropertyChanged(nameof(ShowRangeButtons));
        OnPropertyChanged(nameof(ShowRangePicker));

        if (IsMonth)
        {
            RecomputeMonth();
        }
        else
        {
            Recompute();
        }
    }

    public IReadOnlyList<string> HourLabels =>
        [.. Enumerable.Range(0, 24).Select(h => $"{h:00}:00")];

    public string Range => IsMonth
        ? $"{Anchor:yyyy-MM}"
        : VisibleDays == 1
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
    public void OpenAllDay(AllDayBox? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (entry.TaskId is { } id)
        {
            TaskRequested?.Invoke(id);
            return;
        }

        OpenTaskCommand.Execute(new SlotBox(
            entry.Title, 0, 0, 0, 0, IsTask: false, Color: null,
            StartText: "—", EndText: "—", TaskId: null,
            DayText: Anchor.ToString("dd.MM.yyyy"), entry.SourceId, entry.ExternalId,
            entry.IsDone, entry.CanWrite));
    }

    /// <summary>
    /// Przeciągnięcie zadania na inny dzień i godzinę.
    /// </summary>
    /// <remarks>
    /// Zmienia dokładnie dwie rzeczy — dzień i godzinę — i idzie osobną drogą niż zapis
    /// z okna szczegółu. Długość zostaje: przeciągnięcie przesuwa, a nie skraca.
    /// </remarks>
    public async Task MoveAsync(Guid taskId, DateOnly day, double y)
    {
        var time = Time(y);

        try
        {
            await edit.RescheduleAsync(taskId, day, time);
            await log.RecordAsync(
                "Kalendarz: przełożenie", $"{day:yyyy-MM-dd} {time:HH}:{time:mm}");
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
    public async Task MoveEventAsync(SlotBox block, DateOnly day, double y)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (block is not { SourceId: { } source, ExternalId: { } id })
        {
            return;
        }

        var zone = clock.Now.Offset;
        var start = new DateTimeOffset(day.ToDateTime(Time(y)), zone);
        var length = TimeSpan.FromHours(Math.Max(0.25, block.Height / HourHeight));

        try
        {
            var from = Original(block, zone);

            await calendar.SaveEventAsync(
                source, id, new CalendarDraft(block.Title, start, start + length));

            Undo = new MovedEvent(source, id, block.Title, from, from + length);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(UndoText));

            await log.RecordAsync(
                "Kalendarz: przeniesienie wydarzenia",
                $"{block.Title} na {day:yyyy-MM-dd} {start:HH}:{start:mm}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Kalendarz: przeniesienie wydarzenia", block.Title,
                ActivityLevel.Problem, e.Message);
        }

        await RefreshAsync();
    }

    /// <summary>Skąd wydarzenie przyszło — z dnia i godziny widocznych na bloku.</summary>
    private static DateTimeOffset Original(SlotBox block, TimeSpan zone)
    {
        var day = DateOnly.ParseExact(block.DayText, "dd.MM.yyyy", CultureInfo.InvariantCulture);
        var time = TimeOnly.TryParse(block.StartText, CultureInfo.InvariantCulture, out var g)
            ? g
            : TimeOnly.MinValue;

        return new DateTimeOffset(day.ToDateTime(time), zone);
    }

    /// <summary>Ostatnie przeniesienie wydarzenia — do cofnięcia.</summary>
    public sealed record MovedEvent(
        Guid SourceId, string ExternalId, string Title, DateTimeOffset Start, DateTimeOffset End);

    [ObservableProperty]
    public partial MovedEvent? Undo { get; set; }

    public bool CanUndo => Undo is not null;

    public string UndoText => Undo is { } activity
        ? $"Przeniesiono „{activity.Title}”. Cofnąć na {activity.Start:dd.MM} {activity.Start:HH}:{activity.Start:mm}?"
        : string.Empty;

    [RelayCommand]
    private async Task UndoMoveAsync()
    {
        if (Undo is not { } activity)
        {
            return;
        }

        try
        {
            await calendar.SaveEventAsync(
                activity.SourceId, activity.ExternalId,
                new CalendarDraft(activity.Title, activity.Start, activity.End));

            await log.RecordAsync("Kalendarz: cofnięcie przeniesienia", activity.Title);
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
    private const int Step = 5;

    public static TimeOnly Time(double y)
    {
        var minutes = Math.Clamp(y / HourHeight * 60, 0, (24 * 60) - Step);
        var steps = (int)(minutes / Step) * Step;

        return new TimeOnly(steps / 60, steps % 60);
    }

    /// <summary>Kliknięcie w pustą siatkę: nowe zadanie na tym dniu i o tej godzinie.</summary>
    public event Action<DateOnly, TimeOnly>? NewTaskRequested;

    /// <summary>
    /// Zgłoszenie z widoku: klik w puste miejsce kolumny, na wysokości <paramref name="y"/>.
    /// </summary>
    /// <remarks>
    /// Godzina zaokrąglana w dół do kwadransa. Minuta wzięta co do punktu byłaby
    /// udawaną precyzją: trafienie w 14:07 nie znaczy, że ktoś planuje na 14:07.
    /// </remarks>
    public void NewAt(DateOnly day, double y)
    {
        NewTaskRequested?.Invoke(day, Time(y));
    }

    public async Task LoadAsync()
    {
        if (Anchor == default)
        {
            GoToToday();
        }

        await RefreshAsync();
        ScrollToNow();
    }

    /// <summary>
    /// Ostatnio złożona siatka. Trzymana, żeby zmiana szerokości okna nie musiała
    /// jechać do bazy — bloki zależą od szerokości, a dane nie.
    /// </summary>
    private IReadOnlyList<AgendaDay> _days = [];

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Sąsiedzi złożeni pod poprzedni stan przestają być sąsiadami.
        _neighboursBuilt = false;

        // Pole wyboru zakresu dociągane tutaj, a nie w każdym miejscu, które zmienia
        // zakres z osobna: siatka przeliczana jest po każdej takiej zmianie, więc to
        // jedyne miejsce, przez które wszystkie przechodzą.
        RememberRange();

        // Miesiąc liczy własny zakres: siatka zaczyna się w poniedziałek przed pierwszym
        // i kończy w niedzielę po ostatnim, bo tydzień na przełomie jest tygodniem.
        var od = IsMonth ? GridStart() : Anchor;
        var count = IsMonth ? GridDays() : VisibleDays;

        _days = await calendar.AgendaAsync(od, count);

        if (IsMonth)
        {
            RecomputeMonth();
        }
        else
        {
            Recompute();
        }

        var inDb = await calendar.StoredEventCountAsync();
        var onGrid = _days.Sum(d => d.AllDay.Count + d.Timed.Count);

        Summary = $"W bazie {inDb}, na tych dniach {onGrid}. "
            + $"Godziny w strefie {calendar.ZoneName}.";

        // Zła strefa przesuwa wszystko naraz i wygląda przez to jak źle pobrane dane.
        // Nie nadpisujemy kłopotu z pobierania — ten jest świeższy i bardziej konkretny.
        if (Problem is null && calendar.ZoneProblem is { } trouble)
        {
            Problem = trouble;
            OnPropertyChanged(nameof(HasProblem));
        }

        // Zapisujemy tylko przypadek podejrzany: wydarzenia są, a siatka pusta.
        // Wpis przy każdym przerysowaniu zalałby dziennik tym, co widać na ekranie,
        // i utopiłby w tym rzeczy, których nie widać nigdzie indziej.
        if (onGrid == 0 && inDb > 0)
        {
            await log.RecordAsync(
                "Kalendarz: siatka",
                $"w bazie {inDb}, na siatce 0",
                ActivityLevel.Problem,
                $"Zakres {Range}. Wydarzenia są, ale żadne nie wypada na pokazywanych dniach.");
        }

        OnPropertyChanged(nameof(Range));

        // Sąsiedzi składani **z wyprzedzeniem**, na końcu przeliczenia, a nie przy
        // pierwszym ruchu palca. To była cała przyczyna, dla której poprzednie podejście
        // pokazywało pustkę: składanie ruszało dopiero, gdy palec już jechał, szło przez
        // bramę na bazę i potrafiło poczekać dłużej, niż trwa cały gest. Sąsiad docierał
        // więc zawsze po tym, jak przestał być potrzebny.
        //
        // Ceną są dwa dodatkowe odczyty na każde przeliczenie siatki. Drogie by to było,
        // gdyby chodziło o sieć; tu chodzi o bazę na tym samym urządzeniu.
        //
        // **Po oddaniu sterowania, nie w tym samym przebiegu.** Sąsiedzi są potrzebni
        // dopiero wtedy, gdy palec ruszy — a przy pierwszym wczytaniu ekranu nikt nie
        // przejeżdża. Składane tu wprost znaczyło trzy siatki zamiast jednej, zanim
        // cokolwiek się pokaże, i całe trzy na wątku okna. Oddanie sterowania przepuszcza
        // przed nimi rysowanie i dotknięcia, a różnicy nie widać: dwa odczyty z bazy
        // na tym samym urządzeniu mieszczą się między dwiema klatkami.
        //
        // Bez oczekiwania na wynik, bo nikt na niego nie czeka. Znak „sąsiedzi gotowi"
        // podnosi się sam w środku, a do tego czasu przejechanie palcem przeskakuje —
        // czyli zachowuje się tak, jak zachowywało się zawsze, gdy sąsiadów nie było.
        _ = Dispatcher.UIThread.InvokeAsync(
            async () =>
            {
                await PrepareNeighboursAsync();
                OnPropertyChanged(nameof(NeighboursReady));
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Odświeżenie przy starcie: bez wymuszania i bez wpisu, gdy się udało.
    /// </summary>
    /// <remarks>
    /// Siatka składa się z tego, co w bazie, a odświeżenie zeszło z drogi do gotowości
    /// i biegnie obok niej — więc gdy dojdzie, nikt jej o tym nie mówi. Zapis kalendarzy
    /// nie podnosi znaku zapisu, bo ten należy do zapisów z okna. Stąd to przerysowanie
    /// tutaj, po pierwszym ekranie: bez niego świeże wydarzenia leżałyby w bazie
    /// i czekały na pierwsze dotknięcie strzałki.
    ///
    /// Bez wymuszania, więc gdy przygotowanie zdążyło odświeżyć swoją drogą, ten
    /// przebieg jest pustym sprawdzeniem odstępu, a nie drugim pobraniem.
    /// </remarks>
    public async Task RefreshFromSourcesAsync()
    {
        var report = await calendar.RefreshAsync();

        Problem = report.Failed > 0
            ? $"Nie udało się odświeżyć {report.Failed} z {report.Failed + report.Sources} kalendarzy."
            : null;

        OnPropertyChanged(nameof(HasProblem));
        await RefreshAsync();
    }

    /// <summary>
    /// Pobranie świeżych wydarzeń na żądanie. Osobno od przerysowania, bo to dwie różne
    /// rzeczy: siatkę składamy z tego, co w bazie, a sieć bywa niedostępna.
    /// </summary>
    [RelayCommand]
    private async Task FetchAsync()
    {
        var report = await calendar.RefreshAsync(force: true);

        Problem = report.Failed > 0
            ? $"Nie udało się odświeżyć {report.Failed} z {report.Failed + report.Sources} kalendarzy."
            : null;

        await log.RecordAsync(
            "Kalendarz: pobranie",
            $"odświeżonych {report.Sources}, wydarzeń {report.Events}, nieudanych {report.Failed}"
                + (report.Folded > 0 ? $", złożonych duplikatów {report.Folded}" : string.Empty),
            report.Failed > 0 ? ActivityLevel.Problem : ActivityLevel.Ok,
            string.Join(Environment.NewLine, report.Problems.Distinct()));

        OnPropertyChanged(nameof(HasProblem));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        Anchor = IsMonth ? Anchor.AddMonths(-1) : Anchor.AddDays(-VisibleDays);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        Anchor = IsMonth ? Anchor.AddMonths(1) : Anchor.AddDays(VisibleDays);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task TodayAsync()
    {
        GoToToday();
        await RefreshAsync();
        ScrollToNow();
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

    /// <summary>
    /// Miesiąc: cały obrazek, bez godzin.
    /// </summary>
    /// <remarks>
    /// Odpowiada na inne pytanie niż reszta. Dzień i tydzień mówią „kiedy dokładnie",
    /// miesiąc mówi „gdzie są zagęszczenia i które dni są puste" — a tego nie widać
    /// z siatki godzinowej, bo ta pokazuje naraz najwyżej siedem dni.
    /// </remarks>
    [RelayCommand]
    private async Task ShowMonth()
    {
        IsMonth = true;

        // Zakotwiczenie na pierwszym dniu miesiąca, nie na dzisiejszym: miesiąc
        // przewija się po miesiącach, a „od 18 września przez miesiąc" nie jest miesiącem.
        Anchor = new DateOnly(Anchor.Year, Anchor.Month, 1);

        OnPropertyChanged(nameof(Range));
        await RefreshAsync();
    }

    private async Task SetDaysAsync(int days)
    {
        var fromMonth = IsMonth;

        IsMonth = false;
        MonthWeeks.Clear();

        VisibleDays = days is 1 or 3 or 7 ? days : 3;

        // Wyjście z miesiąca zostawiało zakotwiczenie na pierwszym dniu, więc „dzień"
        // po „miesiącu" pokazywał pierwszego zamiast dzisiaj.
        if (fromMonth)
        {
            GoToToday();
        }

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
    private void ScrollToNow()
    {
        if (IsMonth)
        {
            return;
        }

        var today = clock.Today;

        if (today < Anchor || today >= Anchor.AddDays(VisibleDays))
        {
            return;
        }

        var hour = Math.Max(0, clock.Now.TimeOfDay.TotalHours - 1);

        ScrollRequested?.Invoke(hour * HourHeight);
    }

    private void GoToToday()
    {
        Anchor = clock.Today;

        if (VisibleDays == 7)
        {
            Anchor = Anchor.AddDays(-(((int)Anchor.DayOfWeek + 6) % 7));
        }
    }

    /// <summary>Dotknięcie wpisu w miesiącu otwiera zadanie.</summary>
    /// <remarks>
    /// Wydarzenia z kalendarza zewnętrznego nie mają tu drogi: w komórce miesiąca nie ma
    /// miejsca na to, co odróżnia wydarzenie od zadania, a otwieranie ich szczegółu tym
    /// samym dotknięciem znaczyłoby dwie różne rzeczy pod jednym gestem. Od tego jest
    /// dzień, o jedno dotknięcie stąd.
    /// </remarks>
    public void OpenMonthEntry(MonthEntry? entry)
    {
        if (entry?.TaskId is { } id)
        {
            TaskRequested?.Invoke(id);
        }
    }

    /// <summary>Dotknięcie dnia w miesiącu schodzi na jego siatkę godzinową.</summary>
    /// <remarks>
    /// Miesiąc mówi „gdzie jest gęsto", a nie „o której". Naturalnym następnym ruchem
    /// jest wejście w dzień, który się właśnie wypatrzyło — a nie wracanie do przycisków
    /// u góry i przewijanie do niego od nowa.
    /// </remarks>
    [RelayCommand]
    private async Task OpenMonthDayAsync(MonthCell? cell)
    {
        if (cell is null)
        {
            return;
        }

        IsMonth = false;
        MonthWeeks.Clear();
        VisibleDays = 1;
        Anchor = cell.Date;

        OnPropertyChanged(nameof(ColumnWidth));
        OnPropertyChanged(nameof(Range));

        await RefreshAsync();
        ScrollToNow();
    }

    /// <summary>Poniedziałek, od którego zaczyna się siatka miesiąca.</summary>
    /// <remarks>
    /// Tydzień na przełomie jest tygodniem: wycięcie z niego dni należących do sąsiada
    /// zrobiłoby dziurę tam, gdzie jej nie ma. Dni spoza miesiąca zostają przygaszone.
    /// </remarks>
    private DateOnly GridStart() => GridStart(Anchor);

    private static DateOnly GridStart(DateOnly anchor)
    {
        var first = new DateOnly(anchor.Year, anchor.Month, 1);

        return first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
    }

    /// <summary>
    /// Ile dni obejmuje siatka miesiąca.
    /// </summary>
    /// <remarks>
    /// Liczone, a nie przyjęte na sztywno jako sześć tygodni. Luty zaczynający się
    /// w poniedziałek mieści się w czterech, a rząd pustych komórek pod nim zabierałby
    /// wysokość wszystkim pozostałym — na telefonie to jedna szósta ekranu na nic.
    /// </remarks>
    private int GridDays() => GridDays(Anchor);

    private static int GridDays(DateOnly anchor)
    {
        var od = GridStart(anchor);
        var end = new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(1);

        // Do niedzieli włącznie po ostatnim dniu miesiąca.
        var days = end.DayNumber - od.DayNumber;

        return days % 7 == 0 ? days : days + (7 - (days % 7));
    }

    /// <summary>Złożenie siatki miesiąca z wczytanych dni.</summary>
    private void RecomputeMonth()
    {
        MonthWeeks.Clear();

        foreach (var week in BuildWeeks(_days, Anchor))
        {
            MonthWeeks.Add(week);
        }
    }

    /// <summary>Tygodnie siatki miesiąca z podanych dni. Bez sieci, bez bazy, bez stanu.</summary>
    /// <remarks>
    /// Wyjęte ze składania bieżącego widoku, żeby dało się złożyć także sąsiedni miesiąc
    /// — ten, który przejechanie palcem odsłania w trakcie ruchu.
    /// </remarks>
    private List<MonthWeek> BuildWeeks(IReadOnlyList<AgendaDay> days, DateOnly anchor)
    {
        var weeks = new List<MonthWeek>();

        var today = clock.Today;
        var month = anchor.Month;
        var cells = new List<MonthCell>(days.Count);

        // Pojemność liczona raz na siatkę, nie raz na komórkę: wszystkie mają tę samą
        // wysokość, bo siatka o jednej kolumnie dzieli swoją równo między tygodnie.
        var slots = FitsInCell(days.Count / 7);

        foreach (var day in days)
        {
            // Całodniowe przed godzinowymi, godzinowe po godzinie. Ten sam porządek,
            // co na siatce tygodnia — inaczej ta sama doba miałaby dwie kolejności
            // zależnie od tego, którym przyciskiem się na nią patrzy.
            var entries = day.AllDay
                .Select(e => new MonthEntry(string.Empty, e.Title, e.TaskId, e.IsDone, e.Color))
                .Concat(day.Timed
                    .OrderBy(s => s.Entry.Start)
                    .Select(s => new MonthEntry(
                        // Godzina tylko tam, gdzie jest na nią miejsce. W komórce szerokiej
                        // na jedną siódmą ekranu telefonu „08:00" zabiera połowę wiersza
                        // i z nazwy zostają dwa znaki — czyli wpis przestaje mówić, czego
                        // dotyczy, żeby powiedzieć, o której. Od godzin jest widok dnia.
                        Narrow ? string.Empty : $"{s.Entry.Start.Hour:D2}:{s.Entry.Start.Minute:D2}",
                        s.Entry.Title,
                        s.Entry.TaskId,
                        s.Entry.IsDone,
                        s.Entry.Color)))
                .ToList();

            var visible = entries.Count > slots ? entries.Take(slots).ToList() : entries;

            cells.Add(new MonthCell(
                day.Date,
                $"{day.Date.Day}",
                day.Date == today,
                day.Date.Month != month,
                visible,
                entries.Count - visible.Count));
        }

        for (var i = 0; i + 7 <= cells.Count; i += 7)
        {
            weeks.Add(new MonthWeek(cells.GetRange(i, 7)));
        }

        return weeks;
    }

    /// <summary>Kolumny zakresu po lewej — tego, który odsłania przejechanie w prawo.</summary>
    public ObservableCollection<CalendarColumn> ColumnsBefore { get; } = [];

    /// <summary>Kolumny zakresu po prawej.</summary>
    public ObservableCollection<CalendarColumn> ColumnsAfter { get; } = [];

    /// <summary>Tygodnie poprzedniego miesiąca.</summary>
    public ObservableCollection<MonthWeek> WeeksBefore { get; } = [];

    /// <summary>Tygodnie następnego miesiąca.</summary>
    public ObservableCollection<MonthWeek> WeeksAfter { get; } = [];

    /// <summary>Czy sąsiedzi są złożeni dla bieżącego stanu siatki.</summary>
    private bool _neighboursBuilt;

    /// <summary>
    /// Złożenie sąsiednich zakresów — tych, które widać w trakcie przejechania.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Robione <b>na żądanie, przy pierwszym ruchu palca</b>, a nie przy każdym
    /// przeliczeniu siatki. Siatka przelicza się po każdym odhaczeniu, po każdej
    /// minucie i po każdym przyjściu synchronizacji; składanie przy okazji dwóch
    /// sąsiadów potroiłoby tę pracę po to, żeby prawie zawsze ją wyrzucić. Przejechanie
    /// zdarza się rzadziej niż przeliczenie i ma na to własną chwilę.
    /// </para>
    /// <para>
    /// Sąsiedzi są <b>obrazkiem</b>, nie drugą siatką: nie da się w nich niczego
    /// dotknąć ani przeciągnąć. Interaktywna jest zawsze ta jedna, na której się stoi
    /// — inaczej każde odhaczenie i każde przeciągnięcie bloku musiałoby wiedzieć,
    /// do której z trzech siatek należy, a to jest ten rodzaj wiedzy, który potem
    /// zostaje wszędzie.
    /// </para>
    /// </remarks>
    /// <summary>Czy sąsiedzi są złożeni i jest co pokazać przy przejechaniu.</summary>
    /// <remarks>
    /// Okno pyta o to <b>przed</b> ruchem. Gdy sąsiadów nie ma, siatka nie idzie za
    /// palcem, tylko przeskakuje — bo iść za palcem znaczyłoby odsłaniać puste tło,
    /// a to jest gorsze od braku ruchu. Najgorszy przypadek jest więc równy temu,
    /// co było przed tą zmianą, a nie gorszy od niego.
    /// </remarks>
    public bool NeighboursReady =>
        IsMonth
            ? WeeksBefore.Count > 0 || WeeksAfter.Count > 0
            : ColumnsBefore.Count > 0 || ColumnsAfter.Count > 0;

    public async Task PrepareNeighboursAsync()
    {
        if (_neighboursBuilt)
        {
            return;
        }

        _neighboursBuilt = true;

        try
        {
            if (IsMonth)
            {
                await BuildMonthNeighbour(Anchor.AddMonths(-1), WeeksBefore);
                await BuildMonthNeighbour(Anchor.AddMonths(1), WeeksAfter);
            }
            else
            {
                await BuildDaysNeighbour(Anchor.AddDays(-VisibleDays), ColumnsBefore);
                await BuildDaysNeighbour(Anchor.AddDays(VisibleDays), ColumnsAfter);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Sąsiad jest ozdobą gestu: bez niego przejechanie odsłania puste tło,
            // czyli to, co było wcześniej. Nie ma powodu, żeby psuł cokolwiek innego.
            _neighboursBuilt = false;

            await log.RecordAsync(
                "Kalendarz: sąsiednie zakresy", Range, ActivityLevel.Problem, e.Message);
        }
    }

    private async Task BuildDaysNeighbour(DateOnly od, ObservableCollection<CalendarColumn> target)
    {
        var columns = BuildColumns(await calendar.AgendaAsync(od, VisibleDays));

        target.Clear();

        foreach (var column in columns)
        {
            target.Add(column);
        }
    }

    private async Task BuildMonthNeighbour(DateOnly anchor, ObservableCollection<MonthWeek> target)
    {
        var days = await calendar.AgendaAsync(GridStart(anchor), GridDays(anchor));
        var weeks = BuildWeeks(days, anchor);

        target.Clear();

        foreach (var week in weeks)
        {
            target.Add(week);
        }
    }

    /// <summary>Złożenie kolumn z ostatnio pobranych dni. Bez sieci i bez bazy.</summary>
    private void Recompute()
    {
        Columns.Clear();

        foreach (var column in BuildColumns(_days))
        {
            Columns.Add(column);
        }

        // Po złożeniu kolumn, bo obie liczą się z tego, co w nich jest.
        OnPropertyChanged(nameof(AllDayHeight));
        OnPropertyChanged(nameof(HasAnyAllDay));
    }

    /// <summary>Kolumny z podanych dni. Wyjęte, żeby dało się złożyć także sąsiedni zakres.</summary>
    private List<CalendarColumn> BuildColumns(IReadOnlyList<AgendaDay> days)
    {
        var today = clock.Today;
        var now = clock.Now.TimeOfDay.TotalHours * HourHeight;

        return days.Select(day => new CalendarColumn(
            day.Date,
            $"{DayNames[((int)day.Date.DayOfWeek + 6) % 7]} {day.Date.Day}",
            day.AllDay.Select(e => new AllDayBox(
                e.Title, e.TaskId, e.SourceId, e.ExternalId, e.IsDone, e.CanWrite)).ToList(),
            day.Timed.Select(Box).ToList(),
            day.Date == today,
            now)).ToList();
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
        if (id is not { } taskId)
        {
            return;
        }

        await edit.CompleteAsync(taskId);
        await RefreshAsync();
    }

    /// <summary>
    /// Odhaczenie wpisu z siatki — zadania albo wydarzenia, w obie strony.
    /// </summary>
    /// <remarks>
    /// Jedno wejście dla obu rodzajów, bo z punktu widzenia ręki to ta sama czynność:
    /// kliknięcie w kwadracik przy bloku. Że pod spodem raz idzie zapis do bazy, a raz
    /// zmiana nazwy w cudzym kalendarzu, jest rzeczą do rozstrzygnięcia tutaj, a nie
    /// w oknie — inaczej okno musiałoby wiedzieć, czym blok jest, żeby wiedzieć, co wołać.
    /// </remarks>
    [RelayCommand]
    private async Task ToggleAsync(SlotBox? block)
    {
        if (block is not { CanComplete: true })
        {
            return;
        }

        if (block.IsTask)
        {
            await (block.IsDone ? ReopenAsync(block.TaskId) : CompleteAsync(block.TaskId));
            return;
        }

        if (block is not { SourceId: { } source, ExternalId: { } external })
        {
            return;
        }

        var what = block.IsDone ? "Kalendarz: zdjęcie ptaszka w Google" : "Kalendarz: odhaczenie w Google";

        try
        {
            await calendar.SetEventDoneAsync(source, external, !block.IsDone);
            await log.RecordAsync(what, block.Title);
        }
        catch (EventGone e)
        {
            // Duch zdjęty z siatki przy odmowie, więc siatka musi się przeliczyć —
            // inaczej zostaje na ekranie aż do najbliższego pełnego odczytu.
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(what, block.Title, ActivityLevel.Problem, e.Message);
            await RefreshAsync();

            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(what, "nie udało się", ActivityLevel.Problem, e.Message);
        }

        await RefreshAsync();
    }

    /// <summary>Czy otwarte w karcie da się odhaczyć.</summary>
    public bool CanCompleteOpened => Opened is { CanComplete: true };

    /// <summary>
    /// Odhaczenie z karty otwartego wydarzenia.
    /// </summary>
    /// <remarks>
    /// Ptaszek przy nazwie, tak samo jak w oknie zadania, a nie przycisk obok „Zapisz".
    /// Odhaczenie dotyczy tej jednej rzeczy, której nazwa stoi obok, a nie karty —
    /// a przycisk stojący obok zapisu kusi, żeby kliknąć go po zmianie tytułu i wtedy
    /// zmiana przepada.
    ///
    /// Karta zamyka się po odhaczeniu, tak samo jak okno zadania: to jest koniec
    /// czynności, po której nie ma czego dalej oglądać.
    /// </remarks>
    [RelayCommand]
    private async Task ToggleOpenedAsync()
    {
        if (Opened is not { CanComplete: true } block)
        {
            return;
        }

        await ToggleAsync(block);
        Opened = null;
    }

    /// <summary>
    /// Rozciągnięcie bloku za dolną krawędź: nowa długość zadania.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Długość jest w modelu oszacowaniem, nie godziną zakończenia — koniec wynika
    /// z początku i długości. Rozciąganie zmienia więc oszacowanie, a nie drugą datę,
    /// i to jest ta sama liczba, którą widać w szczegółach i po której dobiera „Teraz".
    /// Przeciągnięcie krawędzi jest po prostu najszybszym sposobem, żeby ją podać.
    /// </para>
    /// <para>
    /// Najmniej pięć minut: krok siatki. Pociągnięcie krawędzi ponad początek bloku
    /// znaczy „chciałam skrócić" i kończy się najkrótszym blokiem, a nie długością
    /// ujemną albo blokiem, który zniknął pod palcem.
    /// </para>
    /// </remarks>
    public async Task ResizeAsync(SlotBox block, double y)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (block.TaskId is not { } task)
        {
            return;
        }

        var start = Time(block.Top);
        var end = Time(y);
        var minutes = (int)(end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;

        try
        {
            await edit.SetMinutesAsync(task, Math.Max(Step, minutes));
            await log.RecordAsync("Kalendarz: rozciągnięcie", $"{Math.Max(Step, minutes)} min");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Kalendarz: rozciągnięcie", "nie udało się", ActivityLevel.Problem, e.Message);
        }

        await RefreshAsync();
    }

    /// <summary>Zdjęcie ptaszka wprost z siatki — ten sam kwadracik, w drugą stronę.</summary>
    /// <remarks>
    /// Kwadracik był dotąd jednokierunkowy: zaznaczał i przestawał reagować. Wyglądało
    /// to jak zepsuty przycisk, bo zaznaczone pole wyboru z natury obiecuje, że da się
    /// je odznaczyć.
    /// </remarks>
    [RelayCommand]
    private async Task ReopenAsync(Guid? id)
    {
        if (id is not { } taskId)
        {
            return;
        }

        await edit.ReopenAsync(taskId);
        await RefreshAsync();
    }

    /// <summary>Otwarty blok: zadanie idzie do nakładki szczegółu, wydarzenie na kartę obok.</summary>
    /// <remarks>
    /// Wydarzenie z podłączonego kalendarza ma własną kartę, nie okno szczegółu zadania:
    /// należy do czegoś innego i ma inne pola. Odhaczyć i przeciągnąć da się je wprost
    /// na siatce, a karta mówi, co to jest i z którego kalendarza pochodzi.
    /// </remarks>
    [RelayCommand]
    private void OpenTask(SlotBox? block)
    {
        if (block is null)
        {
            return;
        }

        if (block.TaskId is { } id)
        {
            TaskRequested?.Invoke(id);
            return;
        }

        Opened = block;

        OpenedTitle = block.Title;
        OpenedStart = Time(block.StartText);
        OpenedEnd = Time(block.EndText);
        OpenedProblem = null;

        // Przycisków zapisu nie pokazujemy tam, gdzie zapis i tak nie ma dokąd pójść.
        //
        // Pytanie idzie o **to** podłączenie, nie o jego rodzaj. Rodzaj oddawał prawdę
        // dla każdego kalendarza Google, więc kalendarz tylko do odczytu — świąteczny,
        // cudzy udostępniony bez prawa zmian — dostawał przyciski „Zapisz" i „Skasuj"
        // i nie dostawał zdania o tym, że jest do odczytu. Odmowa przychodziła dopiero
        // po naciśnięciu. Prawo zapisu niesie sam wpis, policzone przy składaniu siatki.
        CanEditOpened = block.SourceId is not null
            && block.ExternalId is not null
            && block.CanWrite;

        OnPropertyChanged(nameof(HasOpenedProblem));

        _ = LoadAreasAsync(block);
        _ = LoadPeopleAsync();
    }

    /// <summary>Obszary, do których da się przełożyć wydarzenie — czyli te z kalendarzem.</summary>
    public ObservableCollection<Area> EventAreas { get; } = [];

    /// <summary>
    /// Obszar otwartego wydarzenia. Zmiana przekłada je do kalendarza tamtego obszaru.
    /// </summary>
    /// <remarks>
    /// <para>
    /// To jest <b>to samo pytanie</b>, co „do kogo to należy" przy zadaniu — tyle że
    /// przy wydarzeniu odpowiedź nie ma gdzie usiąść po naszej stronie, bo Google nie
    /// ma pola na obszar. Siedzi więc tam, gdzie i tak siedzi: w kalendarzu. Obszar
    /// wydarzenia to obszar jego kalendarza, a zmiana obszaru to przełożenie do innego.
    /// </para>
    /// <para>
    /// Przy okazji jest to jedyna droga do udostępnienia komuś pojedynczego wydarzenia:
    /// kalendarze udostępnia się w Google, raz, a potem wystarczy odłożyć rzecz na
    /// właściwą półkę. Nie ma tu osobnego „udostępnij" i nie powinno być — byłoby
    /// drugim mechanizmem na to samo, w miejscu, w którym Google ma już swój.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial Area? EventArea { get; set; }

    /// <summary>Zapora przed odbiciem: wczytanie stanu nie jest wyborem użytkowniczki.</summary>
    private bool _ownAreas;

    private async Task LoadAreasAsync(SlotBox block)
    {
        try
        {
            var available = await calendar.AreasWithCalendarAsync();
            var now = block.SourceId is { } source
                ? await calendar.AreaOfCalendarAsync(source)
                : null;

            _ownAreas = true;

            try
            {
                EventAreas.Clear();

                foreach (var area in available)
                {
                    EventAreas.Add(area);
                }

                // Po identyfikatorze, nie po samym obiekcie: lista pochodzi z osobnego
                // odczytu, więc to nie są te same wystąpienia.
                EventArea = available.FirstOrDefault(o => o.Id == now?.Id);
            }
            finally
            {
                _ownAreas = false;
            }

            OnPropertyChanged(nameof(CanMoveOpened));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await log.RecordAsync(
                "Kalendarz: obszary wydarzenia", block.Title, ActivityLevel.Problem, e.Message);
        }
    }

    /// <summary>Osoby, którym da się pokazać wydarzenie. Puste, gdy lista jeszcze pusta.</summary>
    public ObservableCollection<Contact> People { get; } = [];

    /// <summary>Wpisywany adres — do dopisania kogoś, kogo jeszcze na liście nie ma.</summary>
    [ObservableProperty]
    public partial string NewPerson { get; set; } = string.Empty;

    /// <summary>
    /// Pokazanie otwartego wydarzenia jednej osobie.
    /// </summary>
    /// <remarks>
    /// Google dopisuje ją jako gościa: dostaje zaproszenie, wydarzenie ląduje w jej
    /// kalendarzu, może potwierdzić. To jest inna rzecz niż wspólny kalendarz — tam
    /// wszystko pojawia się po cichu i na zawsze, tu jedna rzecz i za wiedzą obu stron.
    /// </remarks>
    [RelayCommand]
    private async Task ShowPersonAsync(Contact? person)
    {
        if (person is null || Opened is not { SourceId: { } source, ExternalId: { } entry } block)
        {
            return;
        }

        try
        {
            var added = await calendar.InviteAsync(source, entry, person.Email);

            await log.RecordAsync(
                "Kalendarz: pokazanie osobie",
                $"{block.Title} → {person.Name}",
                ActivityLevel.Ok,
                added ? null : "ta osoba już była na liście gości");

            OpenedProblem = added
                ? $"Zaproszenie poszło do: {person.Name}."
                : $"{person.Name} już to widzi.";

            OnPropertyChanged(nameof(HasOpenedProblem));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            OpenedProblem = e.Message;
            OnPropertyChanged(nameof(HasOpenedProblem));

            await log.RecordAsync(
                "Kalendarz: pokazanie osobie", block.Title, ActivityLevel.Problem, e.Message);
        }
    }

    /// <summary>
    /// Dopisanie osoby do listy i pokazanie jej wydarzenia w jednym ruchu.
    /// </summary>
    /// <remarks>
    /// Jeden ruch, bo adres wpisuje się dokładnie wtedy, gdy się komuś coś pokazuje —
    /// osobne „zarządzanie listą osób" byłoby ekranem, na który nikt nie wchodzi
    /// zawczasu. Imię bierzemy z części przed małpą; poprawia się je potem, jeśli
    /// w ogóle ma to znaczenie.
    /// </remarks>
    [RelayCommand]
    private async Task AddPersonAsync()
    {
        var address = NewPerson.Trim();

        if (address.Length == 0 || people is null || work is null || logicalClock is null)
        {
            return;
        }

        try
        {
            var person = await people.FindByEmailAsync(address);

            if (person is null)
            {
                person = new Contact(
                    Guid.CreateVersion7(), clock.Now, logicalClock.Next(),
                    address.Split('@')[0], address);

                people.Add(person);
                await work.SaveChangesAsync();
            }

            NewPerson = string.Empty;
            await LoadPeopleAsync();
            await ShowPersonAsync(person);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            OpenedProblem = e.Message;
            OnPropertyChanged(nameof(HasOpenedProblem));

            await log.RecordAsync(
                "Kalendarz: dopisanie osoby", address, ActivityLevel.Problem, e.Message);
        }
    }

    private async Task LoadPeopleAsync()
    {
        if (people is null)
        {
            return;
        }

        try
        {
            // Pobranie przed czyszczeniem: lista wyczyszczona przed oczekiwaniem
            // zostaje pusta, gdy odczyt się nie uda, i wygląda jak brak osób.
            var list = await people.AllAsync();

            People.Clear();

            foreach (var person in list)
            {
                People.Add(person);
            }

            OnPropertyChanged(nameof(HasPeople));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await log.RecordAsync(
                "Kalendarz: lista osób", "odczyt", ActivityLevel.Problem, e.Message);
        }
    }

    public bool HasPeople => People.Count > 0;

    /// <summary>Czy jest dokąd przekładać. Bez przypisanych obszarów pole nie ma sensu.</summary>
    public bool CanMoveOpened => CanEditOpened && EventAreas.Count > 0;

    partial void OnEventAreaChanged(Area? value)
    {
        if (_ownAreas || value?.CalendarId is not { } calendarId)
        {
            return;
        }

        _ = MoveEventAsync(calendarId);
    }

    private async Task MoveEventAsync(Guid calendarId)
    {
        if (Opened is not { SourceId: { } source, ExternalId: { } id } block
            || source == calendarId)
        {
            return;
        }

        try
        {
            await calendar.MoveEventAsync(source, id, calendarId);
            await log.RecordAsync("Kalendarz: przełożenie wydarzenia", block.Title);

            Opened = null;
            await RefreshAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            OpenedProblem = e.Message;
            OnPropertyChanged(nameof(HasOpenedProblem));

            await log.RecordAsync(
                "Kalendarz: przełożenie wydarzenia", block.Title, ActivityLevel.Problem, e.Message);
        }
    }

    private static TimeSpan? Time(string text) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time) ? time : null;

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
        if (Opened is not { SourceId: { } source, ExternalId: { } id } block)
        {
            return;
        }

        if (OpenedStart is not { } od || OpenedEnd is not { } toHour)
        {
            OpenedProblem = "Bez godzin nie ma czego zapisać.";
            OnPropertyChanged(nameof(HasOpenedProblem));
            return;
        }

        try
        {
            var day = DateOnly.ParseExact(block.DayText, "dd.MM.yyyy", CultureInfo.InvariantCulture);
            var zone = clock.Now.Offset;

            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.FromTimeSpan(od)), zone);
            var end = toHour > od
                ? new DateTimeOffset(day.ToDateTime(TimeOnly.FromTimeSpan(toHour)), zone)
                : start.AddMinutes(30);

            await calendar.SaveEventAsync(
                source, id, new CalendarDraft(OpenedTitle, start, end));

            await log.RecordAsync("Kalendarz: zapis wydarzenia", OpenedTitle);

            Opened = null;
            await RefreshAsync();
        }
        catch (EventGone e)
        {
            // Karta nie ma już czego opisywać: wydarzenia nie ma po drugiej stronie,
            // a duch został właśnie zdjęty z siatki. Zostawienie jej otwartej znaczyłoby
            // formularz do czegoś, czego nie ma — i odmowę przy każdej kolejnej próbie.
            Opened = null;

            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Kalendarz: zapis wydarzenia", OpenedTitle, ActivityLevel.Problem, e.Message);

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

    /// <summary>
    /// Skasowanie wydarzenia u źródła.
    /// </summary>
    /// <remarks>
    /// <b>Bez potwierdzenia</b>, choć skasowanie nie wraca. Do dziś trzeba było
    /// zaznaczyć zgodę, bo wydarzenie bywa cudze, a zabranie komuś wpisu z kalendarza
    /// jest nieodwracalne. Ale wydarzenie i zadanie mają być pod ręką tą samą rzeczą,
    /// a zadanie o nic nie pyta — i pytanie tylko przy jednym z dwóch uczy odklikiwać
    /// pytania, przez co psuje się także to, przy którym potwierdzenie ma sens.
    /// Ostrzeżenie zostaje na karcie: ono mówi coś, czego nie widać, i nie kosztuje ruchu.
    /// </remarks>
    [RelayCommand]
    private async Task DeleteOpenedAsync()
    {
        if (Opened is not { SourceId: { } source, ExternalId: { } id })
        {
            return;
        }

        try
        {
            await calendar.DeleteEventAsync(source, id);
            await log.RecordAsync("Kalendarz: skasowanie wydarzenia", OpenedTitle);

            Opened = null;
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

    /// <summary>Otwarte wydarzenie. Puste, gdy karta jest zamknięta.</summary>
    [ObservableProperty]
    public partial SlotBox? Opened { get; set; }

    public bool HasOpened => Opened is not null;

    partial void OnOpenedChanged(SlotBox? value)
    {
        OnPropertyChanged(nameof(HasOpened));
        OnPropertyChanged(nameof(CanCompleteOpened));
    }

    [RelayCommand]
    private void CloseOpened()
    {
        Opened = null;
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
            Recompute();
        }
    }

    private SlotBox Box(AgendaSlot slot)
    {
        var width = ColumnWidth / Math.Max(1, slot.Columns);

        return new SlotBox(
            slot.Entry.Title,
            slot.Entry.StartHour * HourHeight,
            slot.Entry.Hours * HourHeight,
            slot.Column * width,
            width - 2,
            slot.Entry.Kind == AgendaKind.Task,
            slot.Entry.Color,

            // Godziny z wpisu, nie z pozycji na siatce: wpis przycięty do dnia ma
            // północ na krawędzi, a pokazać trzeba to, co jest umówione.
            slot.Entry.Start.ToString("HH:mm"),
            slot.Entry.End.ToString("HH:mm"),
            slot.Entry.TaskId,
            slot.Entry.Start.ToString("dd.MM.yyyy"),
            slot.Entry.SourceId,
            slot.Entry.ExternalId,
            slot.Entry.IsDone,
            slot.Entry.CanWrite);
    }
}
