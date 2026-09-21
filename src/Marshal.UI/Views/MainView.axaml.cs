using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Marshal.Application.Review;
using Marshal.Application.UseCases;
using Marshal.Domain.Calendar;
using Marshal.Domain.Tasks;
using Marshal.UI.ViewModels;

namespace Marshal.UI.Views;

public partial class MainView : UserControl
{
    /// <summary>
    /// Szerokość, poniżej której nawigacja schodzi na dół.
    /// </summary>
    /// <remarks>
    /// Telefon w pionie to około 360–430 jednostek, więc próg mógłby być niższy —
    /// ale wąskie okno na pulpicie ma ten sam problem co telefon, a nie ma powodu,
    /// żeby rozstrzygało o tym urządzenie zamiast miejsca, które faktycznie jest.
    /// </remarks>
    private const double NarrowView = 720;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            WirePicker();
            RevealReady();
        };

        // Klawisze łapane w drodze w dół: inaczej kontrolka pod kursorem zjada
        // zdarzenie, zanim okno zdąży cokolwiek z nim zrobić.
        //
        // Kółka **nie** łapiemy. Przekierowanie go do okna szczegółu odbierało obrót
        // rozwiniętej liście godzin, czyli psuło wybieranie godziny — a zamknięte pole
        // daty ani godziny kółka nie zjada, więc przewijanie okna działa samo.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(ContextRequestedEvent, NaMenu, RoutingStrategies.Bubble);

        // Cofnięcie systemowe — na Androidzie przycisk albo gest wstecz. Ta sama
        // odpowiedź co Escape, bo to jest to samo pytanie: „zamknij to, co na wierzchu".
        Back.Handler = Undo;

        // **Także na oknie.** Klawisz idzie drogą od okna do tego, co ma skupienie —
        // a gdy skupienia nie ma nic, droga kończy się na oknie i ten widok nie leży
        // na niej wcale. Tak jest zaraz po otwarciu karty wydarzenia kliknięciem
        // w siatkę: Escape nie zamykał jej, bo ta obsługa nigdy się nie odzywała.
        AttachedToVisualTree += (_, _) =>
            TopLevel.GetTopLevel(this)?.AddHandler(
                KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        // Układ dobierany z faktycznej szerokości, nie z platformy: obrót telefonu
        // i zwężenie okna to ta sama zmiana.
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.IsNarrow = e.NewSize.Width < NarrowView;
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Zdjęcie zasłony startowej, gdy model widoku wreszcie jest.
    /// </summary>
    /// <remarks>
    /// Bez kontekstu danych każde powiązanie „IsVisible" wraca do wartości domyślnej,
    /// czyli widoczne — a to znaczy wszystkie ekrany naraz plus pusty formularz
    /// zadania. Zasłona trwa dokładnie tyle, ile ta chwila.
    /// </remarks>
    private void RevealReady()
    {
        if (this.FindControl<Panel>("ZaslonaStartu") is { } scrim)
        {
            scrim.IsVisible = DataContext is null;
        }
    }

    private ScrollViewer? _grid;

    /// <summary>Przesunięcie, które czeka na zmierzenie siatki. Null, gdy nic nie czeka.</summary>
    private double? _target;

    /// <summary>
    /// Podpięcie przewijania siatki kalendarza pod prośby modelu widoku.
    /// </summary>
    /// <remarks>
    /// Odpięcie przed podpięciem, bo podstawienie modelu potrafi się powtórzyć,
    /// a druga subskrypcja przewijałaby siatkę dwa razy — przy trzeciej i czwartej
    /// przestaje to być niewidoczne.
    /// </remarks>
    private void WireCalendar(MainViewModel model)
    {
        _grid ??= this.FindControl<ScrollViewer>("SiatkaKalendarza");
        _headings ??= this.FindControl<ScrollViewer>("NaglowkiDni");
        _hours ??= this.FindControl<ScrollViewer>("PasGodzin");
        _gridBefore ??= this.FindControl<ScrollViewer>("SiatkaPrzed");
        _gridAfter ??= this.FindControl<ScrollViewer>("SiatkaPo");
        _calendar = model.Calendar;
        _detail = model.Detail;

        model.Calendar.ScrollRequested -= OnScrollRequest;
        model.Calendar.ScrollRequested += OnScrollRequest;

        if (_grid is not null)
        {
            _grid.LayoutUpdated -= OnLayout;
            _grid.LayoutUpdated += OnLayout;

            // Nagłówki jadą w poziomie za siatką. Pionowo stoją — o to właśnie chodzi,
            // bo inaczej uciekały do góry przy pierwszym obrocie kółka i po kilku
            // godzinach dnia nie było wiadomo, na którą kolumnę się patrzy.
            _grid.ScrollChanged -= OnGridScroll;
            _grid.ScrollChanged += OnGridScroll;
            _grid.SizeChanged -= OnWidthChange;
            _grid.SizeChanged += OnWidthChange;

            // Pierwsze podanie szerokości: zdarzenie rozmiaru potrafi wypaść przed
            // podstawieniem modelu, a wtedy siatka zostałaby na szerokości zapasowej.
            SetWidth(_grid.Bounds.Width);
        }

        // To samo dla wysokości miesiąca. Siatka tygodni zgłasza się przy wczytaniu,
        // a to bywa **przed** podstawieniem modelu — wtedy jej meldunek trafiał donikąd
        // i komórka zostawała przy pojemności zapasowej do pierwszej zmiany rozmiaru
        // okna. Na telefonie, gdzie okna się nie zmienia, znaczyło to „nigdy".
        if (_monthGrid is { Bounds.Height: > 0 } weeks)
        {
            _calendar.SetMonthHeight(weeks.Bounds.Height);
        }
    }

    /// <summary>Model szczegółu zadania. Podstawiany razem z resztą, przy zmianie kontekstu.</summary>
    private TaskDetailViewModel? _detail;

    /// <summary>
    /// Przyciski okna szczegółu wołane wprost.
    /// </summary>
    /// <remarks>
    /// Przez polecenia kliknięcie w „Zapisz" kończyło się niczym i nie zostawiało śladu
    /// nawet w pierwszej linijce zapisu — czyli do metody w ogóle nie docierało.
    /// Tu wyjątek też ma dokąd trafić: bez tego byłaby to ta sama pułapka, tylko
    /// przeniesiona o warstwę niżej.
    /// </remarks>
    private void SaveTask(object? sender, RoutedEventArgs e) =>
        OnDetail("Zadanie: zapis z okna", m => m.SaveAsync());

    private void CompleteTask(object? sender, RoutedEventArgs e) =>
        OnDetail("Zadanie: odhaczenie z okna", m => m.CompleteAsync());

    private void DeleteTask(object? sender, RoutedEventArgs e) =>
        OnDetail("Zadanie: do kosza z okna", m => m.TrashAsync());

    private void CloseTask(object? sender, RoutedEventArgs e) =>
        OnDetail("Zadanie: zamknięcie okna", m =>
        {
            m.Close();
            return Task.CompletedTask;
        });

    /// <summary>Enter w nazwie zadania zapisuje — tak jak w każdym polu z jedną linijką.</summary>
    private void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            OnDetail("Zadanie: zapis z klawisza", m => m.SaveAsync());
        }
    }

    /// <summary>
    /// Przejechanie palcem w bok przewija kalendarz o cały zakres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Strzałki u góry zostają, bo na myszy są szybsze. Ale na telefonie cały widok
    /// jest o zakresie — tydzień, trzy dni, miesiąc — a jedyną drogą do sąsiedniego
    /// było wycelowanie w przycisk szerokości kciuka.
    /// </para>
    /// <para>
    /// <b>Palec słuchany gestem, nie wskaźnikiem.</b> Dwie poprzednie wersje śledziły
    /// zdarzenia wskaźnika i łapały jeden ruch na kilkadziesiąt. Powód jest w środku
    /// biblioteki: gdy rozpoznawacz gestu przewijania uzna dotyk za swój, przejmuje
    /// wskaźnik i od tej chwili zdarzenia wskaźnika <b>przestają iść drzewem</b> —
    /// idą wprost do niego. Ruch, który miał zostać przejechaniem, urywa się po
    /// kilku pikselach, a przejechanie widać tylko wtedy, gdy palec zdążył zrobić
    /// całą drogę, zanim przewijanie się zorientowało. Stąd „raz na kilkadziesiąt".
    /// </para>
    /// <para>
    /// Ścigać się z tym nie ma jak, bo rozpoznawacz siatki jest bliżej palca i zawsze
    /// będzie pierwszy. Zamiast tego słuchamy <b>jego</b>: przesunięcia gestu idą
    /// w górę drzewa jako zdarzenia i przechodzą przez nas tak czy owak. Kalendarz
    /// nie odbiera więc przewijania, tylko sumuje to, co przewijanie i tak ogłasza.
    /// </para>
    /// <para>
    /// Własny rozpoznawacz na tym samym panelu jest po to, żeby gest miał się z czego
    /// wziąć w miesiącu: tam nie ma przewijanej siatki, więc nie byłoby czego słuchać.
    /// W dniu i tygodniu pierwszy jest ten z siatki — i tak ma być, bo to on przewija
    /// w pionie.
    /// </para>
    /// <para>
    /// <b>Mysz zostaje przy wskaźniku.</b> Gestu przewijania nie wytwarza, a jej nikt
    /// nie przejmuje — tam śledzenie ruchu działało od początku i nie ma powodu go
    /// ruszać.
    /// </para>
    /// </remarks>
    private void CalendarAreaReady(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control area)
        {
            return;
        }

        // Loaded potrafi przyjść po każdym powrocie na ekran, a dwa rozpoznawacze
        // na jednym panelu liczyłyby ten sam ruch dwa razy.
        if (area.GestureRecognizers.Count == 0)
        {
            area.GestureRecognizers.Add(new ScrollGestureRecognizer
            {
                CanHorizontallyScroll = true,

                // W pionie nie: panel obejmuje też siatkę godzinową, a przejęcie
                // pionu odebrałoby jej przewijanie.
                CanVerticallyScroll = false,
            });
        }

        // **Także obsłużone.** Siatka godzinowa zjada przesunięcia, dopóki ma je jak
        // zużyć na przewijanie — a skoro zjada, to do nas nie docierają. Stąd wzięło
        // się „działa dopiero, gdy pasek dojedzie do końca": tam siatka przestaje
        // mieć co przewijać, przestaje oznaczać zdarzenia jako obsłużone i dopiero
        // wtedy je widzieliśmy. Przejechanie w bok nie odbiera przewijania niczego,
        // bo osobno pilnuje, żeby ruch był wyraźnie poziomy.
        area.AddHandler(
            Gestures.ScrollGestureEvent, AreaGesture, RoutingStrategies.Bubble, handledEventsToo: true);

        area.AddHandler(
            Gestures.ScrollGestureEndedEvent, AreaGestureDone,
            RoutingStrategies.Bubble, handledEventsToo: true);

        area.AddHandler(PointerPressedEvent, AreaPressed, RoutingStrategies.Tunnel);
        area.AddHandler(PointerMovedEvent, AreaMoved, RoutingStrategies.Tunnel);
        area.AddHandler(PointerReleasedEvent, AreaReleased, RoutingStrategies.Tunnel);

        _calendarArea = area;
        _previewBefore = this.FindControl<Panel>("PodgladPrzed");
        _previewAfter = this.FindControl<Panel>("PodgladPo");
    }

    /// <summary>
    /// Siatka tygodni miesiąca melduje swoją wysokość.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Na rozmiarze, nie na układzie. Układ przychodzi przy każdym przebiegu i chodzenie
    /// wtedy po drzewie kontrolek kosztowałoby przy sześciu tygodniach po siedem komórek
    /// za każdym razem; rozmiar przychodzi wtedy, kiedy jest o czym mówić — czyli gdy
    /// okno albo liczba tygodni naprawdę się zmieniły.
    /// </para>
    /// <para>
    /// Pierwsze podanie tutaj, bo zdarzenie rozmiaru potrafi wypaść przed podstawieniem
    /// modelu — dokładnie tak samo jak przy szerokości siatki godzinowej, gdzie ten sam
    /// brak zostawiał kolumny na szerokości zapasowej.
    /// </para>
    /// </remarks>
    private void MonthGridReady(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control grid)
        {
            return;
        }

        _monthGrid = grid;

        grid.SizeChanged -= OnMonthHeightChange;
        grid.SizeChanged += OnMonthHeightChange;

        _calendar?.SetMonthHeight(grid.Bounds.Height);
    }

    /// <summary>Siatka tygodni miesiąca. Trzymana, żeby podać jej wysokość po modelu.</summary>
    private Control? _monthGrid;

    private void OnMonthHeightChange(object? sender, SizeChangedEventArgs e) =>
        _calendar?.SetMonthHeight(e.NewSize.Height);

    /// <summary>Podglądy sąsiednich zakresów. Widoczne wyłącznie w trakcie przejechania.</summary>
    private Panel? _previewBefore;

    private Panel? _previewAfter;

    /// <summary>Żeby brak warstw trafił do dziennika raz, a nie przy każdym ruchu palca.</summary>
    private bool _missingLayersLogged;

    /// <summary>Panel z siatkami. Trzymany do przesunięcia przy zmianie zakresu.</summary>
    private Control? _calendarArea;

    /// <summary>Godziny z lewej i obie siatki sąsiadów — ich pion doganiamy ręcznie.</summary>
    private ScrollViewer? _hours;

    private ScrollViewer? _gridBefore;

    private ScrollViewer? _gridAfter;

    /// <summary>Ile trzeba przejechać w bok, żeby to było przejechanie, a nie przewijanie.</summary>
    /// <remarks>
    /// Sześćdziesiąt punktów to około jednej szóstej szerokości telefonu: za dużo, żeby
    /// wyszło przy pionowym przewijaniu, za mało, żeby trzeba było brać rozmach.
    /// </remarks>
    private const double SwipeThreshold = 60;

    /// <summary>Od ilu punktów w bok siatka zaczyna iść za palcem.</summary>
    /// <remarks>
    /// Dwanaście, bo tyle mieści się w drgnięciu ręki przy przewijaniu w pionie.
    /// Niżej siatka drgałaby w bok przy każdym ruchu po godzinach.
    /// </remarks>
    private const double TrackThreshold = 12;

    /// <summary>Zsumowane przesunięcie bieżącego gestu.</summary>
    private Vector _gesture;

    /// <summary>Czy ten gest już przeskoczył. Jeden ruch to jeden zakres.</summary>
    /// <remarks>Dotyczy wyłącznie drogi bez sąsiadów, gdzie rozstrzyga się w trakcie ruchu.</remarks>
    private bool _gestureSpent;

    /// <summary>
    /// Czy bieżący gest jest już rozliczony i dalsze przesunięcia go nie dotyczą.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Gest nie kończy się na podniesieniu palca.</b> Po nim idzie jeszcze rozpęd
    /// i rozpoznawacz przez sekundę albo dwie sypie kolejnymi przesunięciami — tak
    /// dojeżdża każda lista, po której się przejedzie. Przejechanie rozlicza się
    /// na podniesieniu palca, bo wtedy wiadomo, czy dociągnięto dość daleko; te późniejsze
    /// przesunięcia zbierały się więc w <b>nowy</b> gest i ciągnęły w bok świeżo
    /// pokazaną siatkę.
    /// </para>
    /// <para>
    /// Rozpęd rzadko wystarcza na drugi zakres, ale na ruszenie siatki wystarcza zawsze:
    /// próg śledzenia jest niski, a próg przeskoku wysoki. Siatka odsłaniała więc skrawek
    /// sąsiada i wracała — czyli półtorej sekundy po udanym przejechaniu ekran szarpał
    /// w bok i z powrotem, bez żadnego dotknięcia.
    /// </para>
    /// <para>
    /// <b>Znak zdejmuje wyłącznie nowe dotknięcie</b>, a nie koniec gestu ogłoszony przez
    /// rozpoznawacz — i to jest tu sedno, bo pierwsze podejście zdejmowało go właśnie
    /// tam i nie zmieniło niczego. Kiedy rozpoznawacz ogłasza koniec, nie jest
    /// powiedziane: bywa to podniesienie palca, a nie wygaśnięcie rozpędu. Jeśli
    /// przychodzi na podniesieniu, to zdejmuje znak <b>przed</b> rozpędem, czyli
    /// dokładnie przed tym, przed czym miał chronić. Nowe dotknięcie jest jednoznaczne:
    /// palec na szkle to nowy gest, cokolwiek działo się przedtem.
    /// </para>
    /// </remarks>
    private bool _gestureOver;

    /// <summary>
    /// Siatka idzie za palcem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Przesunięcia z rozpoznawacza są przyrostowe i mają znak odwrotny do ruchu palca:
    /// tak liczy je przewijanie, bo dodaje je wprost do swojego położenia. Stąd
    /// odwrócenie — reszta liczy w tym, dokąd pojechał palec.
    /// </para>
    /// <para>
    /// Rozstrzygnięcie zapada dopiero przy puszczeniu, a nie po przekroczeniu progu
    /// w trakcie ruchu. Przeskok w połowie gestu jest tym, co widać jako „przeskakuje
    /// z widoku na widok": ekran zmienia się pod palcem, który jeszcze jedzie, i nie
    /// ma już jak się z tego wycofać. Tak się nie zachowuje żadna rzecz, którą się
    /// przesuwa — przesuwana idzie za ręką i dopiero po puszczeniu albo dojeżdża,
    /// albo wraca.
    /// </para>
    /// <para>
    /// Czego to nadal <b>nie</b> daje: widoku sąsiedniego zakresu w trakcie gestu.
    /// Odsłania się puste tło, bo złożona jest tylko jedna siatka. Pokazanie sąsiada
    /// znaczyłoby składanie trzech naraz i utrzymywanie ich w zgodzie przy każdym
    /// odhaczeniu i przeciągnięciu bloku — czyli przepisanie kalendarza na karuzelę.
    /// </para>
    /// </remarks>
    private void AreaGesture(object? sender, ScrollGestureEventArgs e)
    {
        // Rozpęd po rozliczonym przejechaniu nie należy już do niczego.
        if (_gestureOver)
        {
            return;
        }

        _gesture += e.Delta;

        var sideways = -_gesture.X;
        var vertical = -_gesture.Y;

        // W bok wyraźnie bardziej niż w pionie — inaczej dojeżdżanie do wieczora
        // ciągnęłoby siatkę w bok przy każdym ukośnym ruchu.
        if (Math.Abs(sideways) < TrackThreshold || Math.Abs(sideways) < 1.5 * Math.Abs(vertical))
        {
            return;
        }

        // **Za palcem tylko wtedy, gdy jest co odsłonić.** Bez złożonych sąsiadów
        // przeciąganie pokazuje puste tło, a to jest gorsze od braku ruchu: udaje
        // płynność i pokazuje dziurę tam, gdzie powinien być następny tydzień.
        // Wtedy siatka po prostu przeskakuje — czyli robi to, co robiła zawsze.
        if (_calendar?.NeighboursReady != true)
        {
            if (!_gestureSpent)
            {
                _gestureSpent = Skip(sideways, vertical);
            }

            return;
        }

        Watch();
        Move(sideways);
    }

    private void AreaGestureDone(object? sender, ScrollGestureEndedEventArgs e) =>
        FinishGesture();

    /// <summary>Ustawienie siatki na zadanym przesunięciu — bez animacji, wprost za palcem.</summary>
    private void Move(double sideways)
    {
        if (_calendarArea is not { Bounds.Width: > 0 } area)
        {
            return;
        }

        RevealNeighbours(area.Bounds.Width);

        _gridOffset ??= new TranslateTransform();
        area.RenderTransform = _gridOffset;

        // Ograniczone do szerokości: dalej i tak nie ma czego odsłaniać, a siatka
        // wyjechana poza ekran wygląda na zgubioną.
        _gridOffset.X = Math.Clamp(sideways, -area.Bounds.Width, area.Bounds.Width);
    }

    /// <summary>
    /// Pokazanie sąsiednich zakresów po bokach siatki.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stoją w tym samym panelu, co kolumny, odsunięte o szerokość pasa siatki w lewo
    /// i w prawo — czyli o tyle, ile jedzie za palcem, bez kolumny godzin.
    /// Przesuwa się <b>panel</b>, więc jadą razem z nią — palec odsłania to, co naprawdę
    /// jest obok, a nie puste tło. Wcześniej przejechanie pokazywało pustkę i wyglądało
    /// to jak wysunięcie kalendarza znikąd.
    /// </para>
    /// <para>
    /// Dane sąsiadów składane są przy pierwszym ruchu palca, nie przy każdym przeliczeniu
    /// siatki: siatka przelicza się po każdym odhaczeniu i po każdej minucie, a sąsiedzi
    /// byliby wtedy prawie zawsze wyrzuceni bez użycia.
    /// </para>
    /// </remarks>
    private void RevealNeighbours(double width)
    {
        // Warstwy odszukiwane przy pierwszym użyciu, nie przy wczytaniu panelu, i to
        // jest zabezpieczenie przed jedyną przyczyną, której poprzednim razem nie
        // wykluczyłem: gdyby odszukanie po nazwie zawiodło, pola zostałyby puste,
        // podglądy nigdy nie zapaliłyby się i wyglądałoby to dokładnie tak samo jak
        // brak danych. Zapis w dzienniku rozdziela te dwie przyczyny.
        _previewBefore ??= this.FindControl<Panel>("PodgladPrzed");
        _previewAfter ??= this.FindControl<Panel>("PodgladPo");

        if ((_previewBefore is null || _previewAfter is null) && !_missingLayersLogged)
        {
            _missingLayersLogged = true;

            _ = Try("Kalendarz: podgląd sąsiadów", () =>
                DataContext is MainViewModel model
                    ? model.Journal.RecordAsync(
                        "Kalendarz: podgląd sąsiadów",
                        "nie ma warstw",
                        "Warstwy PodgladPrzed/PodgladPo nie zostały odnalezione w oknie.")
                    : Task.CompletedTask);
        }

        Set(_previewBefore, -width);
        Set(_previewAfter, width);

        static void Set(Panel? preview, double where)
        {
            if (preview is null)
            {
                return;
            }

            if (preview.RenderTransform is not TranslateTransform offset)
            {
                offset = new TranslateTransform();
                preview.RenderTransform = offset;
            }

            offset.X = where;
            preview.IsVisible = true;
        }
    }

    /// <summary>Schowanie sąsiadów. Poza gestem nie mają czego pokazywać.</summary>
    private void HideNeighbours()
    {
        if (_previewBefore is not null)
        {
            _previewBefore.IsVisible = false;
        }

        if (_previewAfter is not null)
        {
            _previewAfter.IsVisible = false;
        }
    }

    /// <summary>
    /// Koniec gestu: albo dojeżdżamy do sąsiedniego zakresu, albo wracamy.
    /// </summary>
    /// <remarks>
    /// Zakres zmienia się <b>przed</b> animacją powrotu, więc świeża siatka dojeżdża
    /// z miejsca, w które dociągnął ją palec. Animowanie jej od krawędzi ekranu
    /// znaczyłoby skok z położenia, w którym była, na krawędź — czyli to samo szarpnięcie,
    /// które ten gest ma usunąć.
    /// </remarks>
    private void FinishGesture()
    {
        var sideways = -_gesture.X;
        var vertical = -_gesture.Y;

        _gesture = default;
        _gestureOver = true;
        _watch?.Stop();

        if (_gestureSpent)
        {
            _gestureSpent = false;
            return;
        }

        var from = _gridOffset?.X ?? 0;

        if (Math.Abs(from) < 0.5)
        {
            // Siatka nigdy nie ruszyła — to nie było przejechanie, tylko przewijanie
            // albo dotknięcie. Nie ma czego kończyć.
            return;
        }

        Skip(sideways, vertical);
        SlideBack(from);
    }

    /// <summary>Sąsiedzi chowani po dojeździe, nie przed nim — inaczej znikliby w ruchu.</summary>
    private async Task AfterCoastAsync(Task coast)
    {
        await coast;
        HideNeighbours();
    }

    /// <summary>
    /// Pilnowanie, żeby siatka nie została przesunięta, gdy koniec gestu nie przyjdzie.
    /// </summary>
    /// <remarks>
    /// Koniec gestu ogłasza biblioteka i w zwykłym przebiegu przychodzi. Gdyby nie
    /// przyszedł — inna wersja, przerwany dotyk, cokolwiek — siatka zostałaby odsunięta
    /// w bok na stałe, a to jest awaria widoczna i nie do naprawienia inaczej niż
    /// zamknięciem aplikacji. Koszt zabezpieczenia to jeden minutnik na gest.
    /// </remarks>
    private DispatcherTimer? _watch;

    private void Watch()
    {
        _watch ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Input,
            (_, _) => FinishGesture());

        _watch.Stop();
        _watch.Start();
    }

    private (Point From, Point To, IPointer Pointer)? _swipe;

    private void AreaPressed(object? sender, PointerPressedEventArgs e)
    {
        // Nowe dotknięcie zaczyna nowy gest, cokolwiek działo się przedtem.
        _gestureOver = false;

        // Gest palca ma własną drogę; tu zostaje mysz, bo jej nikt nie przejmuje.
        if (sender is not Control area || e.Pointer.Type != PointerType.Mouse)
        {
            _swipe = null;
            return;
        }

        var point = e.GetPosition(area);
        _swipe = (point, point, e.Pointer);
    }

    private void AreaMoved(object? sender, PointerEventArgs e)
    {
        if (_swipe is not { } touch
            || sender is not Control area
            || !ReferenceEquals(touch.Pointer, e.Pointer))
        {
            return;
        }

        var to = e.GetPosition(area);
        _swipe = touch with { To = to };

        var sideways = to.X - touch.From.X;
        var vertical = to.Y - touch.From.Y;

        // Mysz idzie za ręką tak samo jak palec — ten sam próg i ten sam warunek.
        if (!_dragging
            && Math.Abs(sideways) >= TrackThreshold
            && Math.Abs(sideways) >= 1.5 * Math.Abs(vertical))
        {
            Move(sideways);
        }
    }

    private void AreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        var activity = _swipe;
        _swipe = null;

        if (activity is not { } touch || !ReferenceEquals(touch.Pointer, e.Pointer))
        {
            // Palec: zapasowe zakończenie gestu na wypadek, gdyby biblioteka nie
            // ogłosiła jego końca. Gdy siatka nigdy nie ruszyła, nic się nie dzieje.
            FinishGesture();
            return;
        }

        var to = sender is Control area ? e.GetPosition(area) : touch.To;

        var sideways = to.X - touch.From.X;
        var vertical = to.Y - touch.From.Y;

        var from = _gridOffset?.X ?? 0;

        // Obsłużone tylko wtedy, gdy naprawdę przejechano — inaczej zwykłe kliknięcie
        // bloku przestałoby go otwierać.
        e.Handled = Skip(sideways, vertical);

        if (Math.Abs(from) >= 0.5)
        {
            SlideBack(from);
        }
    }

    /// <summary>Czy ruch o tyle punktów jest przejechaniem — i jeśli tak, przeskakuje.</summary>
    private bool Skip(double sideways, double vertical)
    {
        // Przeciąganie bloku też jedzie w bok — i to ono ma wtedy znaczenie, nie zakres.
        if (_dragging || _calendar is null)
        {
            return false;
        }

        // W bok **wyraźnie bardziej** niż w pionie: ukośny ruch przy przewijaniu dnia
        // przeskakiwałby tydzień przy każdej próbie dojechania do wieczora.
        if (Math.Abs(sideways) < SwipeThreshold || Math.Abs(sideways) < 1.5 * Math.Abs(vertical))
        {
            return false;
        }

        // Palec w lewo odsłania to, co po prawej — czyli następny zakres. Tak samo
        // zachowuje się każda lista, po której się przejeżdża.
        _ = Try(
            "Kalendarz: przejechanie",
            () => sideways < 0
                ? _calendar.NextCommand.ExecuteAsync(null)
                : _calendar.PreviousCommand.ExecuteAsync(null));

        return true;
    }

    /// <summary>Jak długo siatka dojeżdża na miejsce po puszczeniu.</summary>
    /// <remarks>
    /// Sto sześćdziesiąt milisekund: dość, żeby ruch był ruchem, a nie podmianą,
    /// i za mało, żeby zdążyło się na niego czekać.
    /// </remarks>
    private static readonly TimeSpan CoastTime = TimeSpan.FromMilliseconds(160);

    /// <summary>Przesunięcie rysowania siatki. Jedno na całe życie okna.</summary>
    /// <remarks>
    /// Przesuwane jest rysowanie, nie układ: siatka zostaje tam, gdzie była, więc nic
    /// się nie przelicza i nic nie zmienia rozmiaru.
    /// </remarks>
    private TranslateTransform? _gridOffset;

    /// <summary>
    /// Dojazd siatki do miejsca — z tego, dokąd dociągnął ją palec, do zera.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jedna animacja na oba zakończenia gestu i to jest sedno. Gdy zakres się zmienił,
    /// świeża siatka dojeżdża z miejsca, w którym zostawił ją palec — wygląda to jak
    /// dociągnięcie tego, co się właśnie przyciągnęło. Gdy się nie zmienił, ta sama
    /// animacja jest powrotem. Ruch jest ten sam, bo z punktu widzenia ręki dzieje się
    /// to samo: rzecz wraca na swoje miejsce.
    /// </para>
    /// <para>
    /// Ruszana jest sama liczba, nie własność, która ją trzyma. Podanie pod animację
    /// panelu i kazanie jej ruszać <c>RenderTransform</c> zapisanym jako operacje
    /// przekształcenia wywracało aplikację przy pierwszym przejechaniu: dla takiej
    /// własności nie ma domyślnego animatora, a wychodzi to na jaw dopiero przy
    /// pierwszej klatce, czyli w trakcie gestu.
    /// </para>
    /// <para>
    /// Całość przez wspólne zabezpieczenie okna. To jest ozdoba — zakres zmienia się
    /// tak czy owak — a ozdoba nie ma prawa zamknąć aplikacji.
    /// </para>
    /// </remarks>
    private void SlideBack(double from) =>
        _ = Try("Kalendarz: dojazd siatki", () => AfterCoastAsync(BackAsync(from)));

    private Task BackAsync(double from)
    {
        if (_gridOffset is not { } offset)
        {
            return Task.CompletedTask;
        }

        var animation = new Animation
        {
            Duration = CoastTime,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(TranslateTransform.XProperty, from) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(TranslateTransform.XProperty, 0d) },
                },
            },
        };

        // Wartość zdejmowana od razu, żeby po animacji nie wrócił na nią stary stan:
        // animacja z wygaszaniem „None" oddaje własność temu, co w niej zapisane.
        offset.X = 0;

        return animation.RunAsync(offset);
    }

    /// <summary>Warstwa linii godzin — pionowy punkt odniesienia dla przeciągania.</summary>
    /// <remarks>
    /// Wszystkie kolumny mają tę samą górną krawędź siatki, więc wystarczy jedna:
    /// wysokość liczona względem niej jest prawdziwa niezależnie od tego, nad którym
    /// dniem stoi wskaźnik.
    /// </remarks>
    private Control? _hourLayer;

    private void HourLayerReady(object? sender, RoutedEventArgs e) =>
        _hourLayer = sender as Control;

    /// <summary>Ile trzeba przejechać, żeby to było przeciąganie, a nie drgnięcie ręki.</summary>
    private const double DragThreshold = 6;

    /// <summary>
    /// Ile palcowi wolno zjechać, zanim minie przytrzymanie.
    /// </summary>
    /// <remarks>
    /// Sześć punktów wystarczy myszy, która stoi tam, gdzie ją zostawisz. Palec przez
    /// cztery dziesiąte sekundy nie stoi nigdy — i każde drgnięcie ponad ten próg
    /// kasowało chwyt, zanim zdążył się zacząć. Stąd osobny, większy luz na ten czas:
    /// po przytrzymaniu wraca zwykły próg, bo wtedy każdy ruch jest już przeciąganiem.
    /// </remarks>
    private const double HoldSlop = 18;

    private SlotBox? _pressed;

    private Point _from;

    /// <summary>Ostatnie znane położenie wskaźnika — podgląd rysuje się także bez zdarzenia.</summary>
    private Point _last;

    private bool _dragging;

    /// <summary>Czy trwające przeciąganie zmienia długość, a nie porę.</summary>
    private bool _stretching;

    /// <summary>Kursory tworzone raz. Nowy przy każdym drgnięciu myszy to nowy zasób systemu.</summary>
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private static readonly Cursor EdgeCursor = new(StandardCursorType.SizeNorthSouth);

    /// <summary>Ile punktów od dolnej krawędzi bloku łapie za jego koniec.</summary>
    /// <remarks>
    /// <para>
    /// Sześć, tyle samo co próg przeciągnięcia. Więcej znaczyłoby, że kwadransowy blok
    /// jest w połowie krawędzią i nie da się go już przesunąć; mniej, że w krawędź
    /// trzeba celować.
    /// </para>
    /// <para>
    /// <b>Tylko myszą.</b> Sześć punktów to dla palca nic — na godzinnym bloku jest to
    /// dolna ósma część, a kwadransowy jest krawędzią prawie w całości. Chwyt, który
    /// na dotyku miał przenieść zadanie, wpadał więc w rozciąganie: kopia stawała
    /// w miejscu i rosła w dół, zamiast iść za palcem. Do tego rozciąganie za krawędź
    /// jest wiedzą z kursora — a kursora na telefonie nie ma, więc nie ma też jak
    /// się o tej krawędzi dowiedzieć. Długość zmienia się tam kartą zadania.
    /// </para>
    /// </remarks>
    private const double EdgeZone = 6;

    /// <summary>
    /// Ile punktów ma godzina na siatce.
    /// </summary>
    /// <remarks>
    /// Ta sama liczba co w modelu kalendarza, powtórzona tutaj świadomie: podgląd
    /// rysuje okno, a nie model, i gdyby sięgał po nią przez model, okno zależałoby
    /// od jego wnętrza po to, żeby narysować prostokąt.
    /// </remarks>
    private const double HourHeight = 48;

    /// <summary>Punkt wewnątrz bloku, za który go złapano.</summary>
    private Point _grip;

    /// <summary>Lewy górny róg chwyconego bloku we współrzędnych okna.</summary>
    private Point? _blockOnScreen;

    private void BlockPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control block || block.Tag is not SlotBox slot)
        {
            return;
        }

        // Tylko lewym przyciskiem. Prawy otwiera menu podręczne, a zapamiętany przy nim
        // blok zostawał w ręku: po usunięciu zadania z menu puszczenie nie miało już
        // dokąd trafić, więc wskaźnik do końca sesji zachowywał się tak, jakby wciąż
        // coś ciągnął — i nie dało się z tym nic zrobić.
        if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressed = slot;
        _pressedBlock = block;
        _pressedPointer = e.Pointer;
        _from = e.GetPosition(this);
        _last = _from;
        _dragging = false;

        // Myszą przeciąga się od razu; palcem dopiero po przytrzymaniu. Na dotyku
        // ruch palca po bloku znaczy najczęściej „przewiń widok", a nie „przenieś to
        // zadanie" — a blok, który przejmuje wskaźnik po sześciu punktach, odbiera
        // przewijanie wszędzie tam, gdzie coś stoi. Czyli w zajęty dzień prawie wszędzie.
        //
        // Wystąpienie rytmu narysowane do przodu przeciąga się tak samo, choć nie jest
        // jeszcze zadaniem: pod spodem zapisuje się nie jego nowy dzień, tylko zmiana
        // przy tym jednym wystąpieniu rytmu. Z punktu widzenia ręki to ta sama czynność
        // i ma być tym samym gestem.
        _dragAllowed = e.Pointer.Type != PointerType.Touch;

        if (!_dragAllowed)
        {
            _longPress?.Stop();
            _longPress = new DispatcherTimer(
                LongPress, DispatcherPriority.Input, (clock, _) =>
                {
                    (clock as DispatcherTimer)?.Stop();
                    LongPressElapsed();
                });

            _longPress.Start();
        }

        // Gdzie **w bloku** wylądowała ręka i gdzie ten blok stoi na ekranie. Jedno
        // i drugie po to, żeby kopia w podglądzie trzymała się tego samego punktu,
        // za który została złapana, zamiast skakać rogiem pod wskaźnik.
        _grip = e.GetPosition(block);
        _blockOnScreen = block.TranslatePoint(new Point(0, 0), this);

        // Za dolną krawędź ciągnie się koniec, nie cały blok. Przy zadaniach i przy
        // zapowiedziach rytmu — te drugie zapisują długość przy swoim jednym dniu.
        // Wydarzenie z cudzego kalendarza zmienia długość świadomą drogą, w karcie.
        _stretching = (slot.TaskId is not null || slot.IsAhead)
            && e.Pointer.Type != PointerType.Touch
            && e.GetPosition(block).Y >= block.Bounds.Height - EdgeZone;
    }

    /// <summary>
    /// Kształt wskaźnika nad blokiem: strzałka albo znak rozciągania przy krawędzi.
    /// </summary>
    /// <remarks>
    /// Bez tego chwyt za koniec bloku jest wiedzą tajemną — nic na ekranie nie mówi,
    /// że dolne sześć punktów robi co innego niż reszta.
    /// </remarks>
    private void BlockHovered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control block || block.Tag is not SlotBox slot)
        {
            return;
        }

        var atEdge = slot.TaskId is not null
            && e.GetPosition(block).Y >= block.Bounds.Height - EdgeZone;

        block.Cursor = atEdge ? EdgeCursor : HandCursor;
    }

    /// <summary>
    /// Koniec chwytu, niezależnie od tego, czy doszło puszczenie.
    /// </summary>
    /// <remarks>
    /// Wołane także wtedy, gdy blok znika spod ręki — po usunięciu zadania z menu
    /// podręcznego albo po przeliczeniu siatki. Bez tego zapamiętany blok zostawał
    /// w polu i każdy następny ruch myszy wyglądał jak przeciąganie.
    /// </remarks>
    /// <summary>Ile trwa przytrzymanie, po którym palec zaczyna przeciągać, a nie przewijać.</summary>
    private static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(400);

    /// <summary>Minutnik przytrzymania i jego wynik. Mysz ma zgodę od razu.</summary>
    private DispatcherTimer? _longPress;

    private bool _dragAllowed = true;

    /// <summary>Blok pod ręką i wskaźnik, którym go trzymamy — potrzebne po przytrzymaniu.</summary>
    private Control? _pressedBlock;

    private IPointer? _pressedPointer;

    /// <summary>
    /// Przytrzymanie minęło: blok jest w ręku.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wskaźnik przejmowany jest <b>tutaj</b>, a nie przy pierwszym ruchu. To jest
    /// różnica między „da się przeciągnąć" a „nie da się" na dotyku: siatka godzinowa
    /// i pasek zakresu mają własne rozpoznawacze gestów, które przejmują wskaźnik,
    /// gdy tylko palec ruszy dalej niż ich próg. Przejęcie dopiero przy ruchu znaczyło,
    /// że ten wyścig wygrywało przewijanie — a przegrany chwyt zgłasza utratę
    /// przechwycenia, czyli blok wypada z ręki i nie dzieje się nic.
    /// </para>
    /// <para>
    /// Blok przygasa od razu po przytrzymaniu, jeszcze przed ruchem. Bez tego jedyną
    /// odpowiedzią na dobrze wykonane przytrzymanie jest brak odpowiedzi — a wtedy
    /// nie da się odróżnić „trzymam za krótko" od „to w ogóle nie działa".
    /// </para>
    /// </remarks>
    /// <summary>Rozpoznawacze przewijania uśpione na czas ciągnięcia, z ich stanem sprzed.</summary>
    private readonly List<(ScrollGestureRecognizer Recognizer, bool Sideways, bool Vertical)> _paused = [];

    /// <summary>
    /// Uśpienie przewijania nad blokiem trzymanym w ręku.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Przejęcie wskaźnika nie wystarcza. Rozpoznawacz przewijania liczy ruch osobno
    /// i po przekroczeniu swojego progu <b>zabiera wskaźnik sobie</b> — a odebranie
    /// przechwycenia kończy u nas chwyt. Widać to jako blok, który rusza się za palcem
    /// przez ułamek sekundy i znika: tyle trwa droga do progu przewijania.
    /// </para>
    /// <para>
    /// Usypiane są wszystkie po drodze od bloku w górę: siatka godzinowa przewija
    /// w pionie, a panel z siatkami w poziomie. Stan sprzed zapamiętany, bo to te same
    /// rozpoznawacze, które za chwilę mają znów działać — nie da się ich postawić
    /// na sztywno, bo jeden z nich jest ustawiony przez nas, a drugi przez Avalonię.
    /// </para>
    /// </remarks>
    private void PauseScrolling(Control block)
    {
        foreach (var element in block.GetSelfAndVisualAncestors().OfType<InputElement>())
        {
            foreach (var recognizer in element.GestureRecognizers.OfType<ScrollGestureRecognizer>())
            {
                _paused.Add((recognizer, recognizer.CanHorizontallyScroll, recognizer.CanVerticallyScroll));
                recognizer.CanHorizontallyScroll = false;
                recognizer.CanVerticallyScroll = false;
            }
        }
    }

    private void ResumeScrolling()
    {
        foreach (var (recognizer, sideways, vertical) in _paused)
        {
            recognizer.CanHorizontallyScroll = sideways;
            recognizer.CanVerticallyScroll = vertical;
        }

        _paused.Clear();
    }

    /// <summary>Zadania ruszamy zawsze, wydarzenia tylko takie, które mają dokąd wrócić.</summary>
    private static bool CanMove(SlotBox slot) =>
        slot.TaskId is not null || (slot.SourceId is not null && slot.ExternalId is not null);

    private void LongPressElapsed()
    {
        _dragAllowed = true;

        if (_pressed is not { } slot || _pressedBlock is not { } block)
        {
            return;
        }

        // Blok, który i tak nie ma dokąd pójść, nie zabiera wskaźnika siatce:
        // przytrzymanie na cudzym wydarzeniu kończyło przewijanie na ten gest
        // i nie dawało w zamian niczego.
        if (!CanMove(slot))
        {
            return;
        }

        _pressedPointer?.Capture(block);
        PauseScrolling(block);
        block.Opacity = 0.7;

        // Kopia pojawia się już teraz, pod palcem, a nie dopiero po przejechaniu progu.
        // Przytrzymanie, po którym nie dzieje się nic widocznego poza przygaszeniem,
        // nie mówi ręce, że blok jest już w niej — a wtedy ruch zaczyna się od zgadywania.
        ShowPreview(_last, slot);
    }

    private void ReleaseBlock()
    {
        _longPress?.Stop();
        ResumeScrolling();
        _dragAllowed = true;

        if (_pressedBlock is { } block)
        {
            block.Opacity = 1;
        }

        _pressedBlock = null;
        _pressedPointer = null;
        _pressed = null;
        _dragging = false;
        _stretching = false;
        HidePreview();
    }

    private void BlockLostPointer(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Control block)
        {
            block.Opacity = 1;
        }

        ReleaseBlock();
    }

    /// <summary>Dzień i wysokość pod wskaźnikiem, przeliczone na współrzędne siatki.</summary>
    /// <remarks>
    /// Z położenia wskaźnika, a nie z tego, co pod nim: przy przechwyconym wskaźniku
    /// zdarzenia trafiają do przeciąganego bloku niezależnie od tego, nad czym stoi.
    /// </remarks>
    private (DateOnly Day, double Height)? Target(Point where)
    {
        if (_calendar is null
            || _hourLayer is null
            || this.FindControl<ItemsControl>("KolumnyDni") is not { } columns
            || this.TranslatePoint(where, columns) is not { } inColumns
            || this.TranslatePoint(where, _hourLayer) is not { } inHours)
        {
            return null;
        }

        var width = _calendar.ColumnWidth + 2;
        var number = Math.Clamp(
            (int)(inColumns.X / width), 0, _calendar.VisibleDays - 1);

        return (_calendar.Anchor.AddDays(number), inHours.Y);
    }

    /// <summary>
    /// Podgląd przeciąganego bloku: co i dokąd.
    /// </summary>
    /// <remarks>
    /// Bez niego przeciąganie jest ruchem w ciemno — o właściwej godzinie dowiadujesz
    /// się dopiero po puszczeniu, czyli po zapisie. Przy wydarzeniach z Google znaczy
    /// to po zapisie w cudzym kalendarzu.
    /// </remarks>
    private void ShowPreview(Point where, SlotBox slot)
    {
        if (this.FindControl<Border>("Podglad") is not { } preview
            || this.FindControl<TextBlock>("PodgladTytul") is not { } title
            || this.FindControl<TextBlock>("PodgladOd") is not { } od
            || this.FindControl<TextBlock>("PodgladDo") is not { } toHour
            || this.FindControl<StackPanel>("PodgladGodziny") is not { } hours
            || Target(where) is not var (day, height))
        {
            return;
        }

        var start = CalendarViewModel.Time(slot.Top);
        var time = CalendarViewModel.Time(height);

        preview.Width = slot.Width;
        preview.Background = slot.Background;
        title.Text = slot.Title;

        if (_stretching)
        {
            // Rozciąganie: blok stoi tam, gdzie stał, i rośnie w dół za wskaźnikiem.
            // Dzień się nie zmienia, więc pokazanie go sugerowałoby, że gdzieś jedzie.
            var end = TimeOnly.FromTimeSpan(
                time.ToTimeSpan() > start.ToTimeSpan()
                    ? time.ToTimeSpan()
                    : start.ToTimeSpan() + TimeSpan.FromMinutes(5));

            preview.Height = Math.Max(
                12, (end.ToTimeSpan() - start.ToTimeSpan()).TotalHours * HourHeight);

            od.Text = $"{start:HH}:{start:mm}";
            toHour.Text = $"{end:HH}:{end:mm}";
            hours.IsVisible = true;

            if (_blockOnScreen is { } corner)
            {
                Canvas.SetLeft(preview, corner.X);
                Canvas.SetTop(preview, corner.Y);
            }
        }
        else
        {
            // Koniec liczony z długości bloku, nie z jego starej godziny: przeciągnięcie
            // przesuwa, a nie skraca. Doba przycięta, żeby blok zaczepiony pod wieczór
            // nie pokazywał godziny z następnego dnia.
            var total = time.ToTimeSpan() + TimeSpan.FromHours(slot.Height / HourHeight);
            var end = TimeOnly.FromTimeSpan(
                total < TimeSpan.FromDays(1) ? total : TimeSpan.FromDays(1) - TimeSpan.FromMinutes(5));

            preview.Height = slot.Height;
            od.Text = $"{day:dd.MM} {time:HH}:{time:mm}";
            toHour.Text = $"{end:HH}:{end:mm}";
            hours.IsVisible = slot.Width >= 120;

            Canvas.SetLeft(preview, where.X - _grip.X);
            Canvas.SetTop(preview, where.Y - _grip.Y);
        }

        preview.IsVisible = true;
    }

    private void HidePreview()
    {
        _blockOnScreen = null;

        if (this.FindControl<Border>("Podglad") is { } preview)
        {
            preview.IsVisible = false;
        }
    }

    private void BlockMoved(object? sender, PointerEventArgs e)
    {
        // Kształt wskaźnika liczony przy każdym ruchu, także bez wciśnięcia: to jedyne
        // miejsce, po którym widać, że dolna krawędź robi co innego niż reszta bloku.
        BlockHovered(sender, e);

        if (_pressed is not { } slot)
        {
            return;
        }

        if (!CanMove(slot))
        {
            return;
        }

        _last = e.GetPosition(this);

        if (!_dragging)
        {
            var slop = _dragAllowed ? DragThreshold : HoldSlop;

            if (Math.Abs(_last.X - _from.X) <= slop
                && Math.Abs(_last.Y - _from.Y) <= slop)
            {
                return;
            }

            // Palec ruszył, zanim minęło przytrzymanie: ten gest należy do przewijania.
            // Blok wypuszczamy z ręki na dobre, żeby przytrzymanie, które minie
            // w połowie przewijania, nie porwało go w locie.
            if (!_dragAllowed)
            {
                ReleaseBlock();
                return;
            }

            _dragging = true;

            if (sender is Control block)
            {
                block.Opacity = 0.5;
                e.Pointer.Capture(block);
            }
        }

        ShowPreview(_last, slot);
    }

    /// <summary>
    /// Puszczenie bloku: nowy dzień z poziomej pozycji, nowa godzina z pionowej.
    /// </summary>
    /// <remarks>
    /// Przeliczenie idzie z położenia wskaźnika, a nie z tego, co jest pod nim: przy
    /// przechwyconym wskaźniku zdarzenia trafiają do przeciąganego bloku niezależnie
    /// od tego, nad czym akurat stoi. Cudze wydarzenia nie dają się przeciągać — zapis
    /// do kalendarza Google idzie świadomą drogą, przez kartę, a nie przez omsknięcie ręki.
    /// </remarks>
    private void BlockReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Stan odczytany **przed** oddaniem wskaźnika. Oddanie zgłasza utratę
        // przechwycenia, a ta kończy chwyt i zeruje te trzy pola — czytane po niej
        // dałyby każde puszczenie jako kliknięcie i przeciąganie przestałoby działać.
        var slot = _pressed;
        var dragged = _dragging;
        var stretched = _stretching;

        ReleaseBlock();

        if (sender is Control block)
        {
            block.Opacity = 1;
            e.Pointer.Capture(null);
        }

        // Puszczenie bez przejechania progu jest kliknięciem. Przycisk robił to za nas,
        // ale przy okazji zjadał wciśnięcie i przeciąganie nie miało jak się zacząć.
        if (!dragged)
        {
            if (slot is not null)
            {
                _calendar?.OpenTaskCommand.Execute(slot);
            }

            return;
        }

        if (_calendar is null || slot is null)
        {
            return;
        }

        if (Target(e.GetPosition(this)) is not var (day, height) || _calendar is null)
        {
            return;
        }

        if (stretched)
        {
            // Zapowiedź rytmu zapisuje długość przy swoim dniu, zadanie — u siebie.
            // Z punktu widzenia ręki to ten sam ruch; pod spodem dwie różne rzeczy,
            // bo jednej z nich jeszcze nie ma.
            _ = slot is { RhythmId: { } series, RhythmDate: { } which }
                ? Try(
                    "Kalendarz: długość wystąpienia",
                    () => _calendar.ResizeOccurrenceAsync(series, which, slot.Top, height))
                : Try("Kalendarz: rozciągnięcie", () => _calendar.ResizeAsync(slot, height));

            return;
        }

        // Wystąpienie rytmu przekłada się przez regułę, bo zadania jeszcze nie ma.
        if (slot is { RhythmId: { } rhythm, RhythmDate: { } occurrence })
        {
            _ = Try(
                "Kalendarz: przełożenie wystąpienia",
                () => _calendar.MoveOccurrenceAsync(rhythm, occurrence, day, height));

            return;
        }

        // Zadanie idzie naszą drogą, wydarzenie — prosto do kalendarza, z którego
        // pochodzi. To druga rzecz, nie ta sama z innym zapisem.
        _ = slot.TaskId is { } task
            ? Try("Kalendarz: przełożenie", () => _calendar.MoveAsync(task, day, height))
            : Try(
                "Kalendarz: przeniesienie wydarzenia",
                () => _calendar.MoveEventAsync(slot, day, height));
    }



    /// <summary>
    /// Kwadracik na siatce — w obie strony.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zdarzenie zatrzymujemy tutaj: bez tego wciśnięcie doszłoby do bloku pod spodem
    /// i zaczęłoby przeciąganie, a odhaczenie skończyłoby się przełożeniem zadania
    /// o kilka minut.
    /// </para>
    /// <para>
    /// Zaznaczony kwadracik zdejmuje ptaszek. Do dziś nie robił nic, a pole wyboru,
    /// którego nie da się odznaczyć, wygląda jak zepsute — i zostawiało omyłkowe
    /// odhaczenie bez żadnej drogi odwrotu poza bazą.
    /// </para>
    /// </remarks>
    /// <summary>Gdzie i czym zaczęło się dotknięcie kwadracika.</summary>
    private (SlotBox Block, Point From, IPointer Pointer)? _touchedBlock;

    /// <remarks>
    /// Tak samo jak przy pustej siatce: odhacza puszczenie, a nie naciśnięcie.
    /// Kwadracik jest mały, ale leży na bloku zadania, a przewijanie zaczyna się tam,
    /// gdzie akurat wylądował palec — odhaczenie zadania przy próbie przesunięcia
    /// widoku jest gorsze od nieodhaczenia go wcale.
    /// </remarks>
    private void CompleteOnGrid(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _touchedBlock = null;

        if (sender is not Control checkbox || checkbox.Tag is not SlotBox block)
        {
            return;
        }

        _touchedBlock = (block, e.GetPosition(checkbox), e.Pointer);
    }

    private void CompleteReleased(object? sender, PointerReleasedEventArgs e)
    {
        var start = _touchedBlock;
        _touchedBlock = null;

        if (start is not { } touch
            || sender is not Control checkbox
            || _calendar is null
            || !ReferenceEquals(touch.Pointer, e.Pointer))
        {
            return;
        }

        var end = e.GetPosition(checkbox);

        if (Math.Abs(end.X - touch.From.X) > DragThreshold
            || Math.Abs(end.Y - touch.From.Y) > DragThreshold)
        {
            return;
        }

        // Jedno polecenie na oba rodzaje bloku i oba kierunki. Okno nie musi wiedzieć,
        // czy pod spodem idzie zapis do bazy, czy zmiana nazwy w cudzym kalendarzu.
        _ = Try("Kalendarz: kwadracik", () => _calendar.ToggleCommand.ExecuteAsync(touch.Block));
    }

    private void CompleteAbandoned(object? sender, PointerCaptureLostEventArgs e) =>
        _touchedBlock = null;

    /// <summary>Kliknięcie w przyciemnione tło zamyka okno szczegółu.</summary>
    private void DetailBackground(object? sender, PointerPressedEventArgs e) => _detail?.Close();

    /// <summary>Kliknięcie obok karty wydarzenia zamyka ją.</summary>
    /// <remarks>
    /// To samo, co przy karcie zadania, i z tego samego powodu: karta zasłania kalendarz,
    /// a najbliższą rzeczą, w którą trafia ręka chcąca wrócić do siatki, jest siatka.
    /// </remarks>
    private void EventBackground(object? sender, PointerPressedEventArgs e) =>
        _calendar?.CloseOpenedCommand.Execute(null);

    /// <summary>Zatrzymanie kliknięcia na ramce okna, żeby nie doszło do tła.</summary>
    private void StopClick(object? sender, PointerPressedEventArgs e) =>
        e.Handled = true;

    /// <summary>
    /// Escape zamyka to, co jest otwarte na wierzchu.
    /// </summary>
    /// <remarks>
    /// Kolejność od najbardziej wierzchniego: szczegół zadania, potem karta wydarzenia,
    /// potem lista „Więcej". Zamykanie wszystkiego naraz zabierałoby okno, którego
    /// nikt nie chciał zamykać.
    /// </remarks>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = Undo();
        }
    }

    /// <summary>
    /// Cofnięcie: zamknięcie tego, co jest otwarte na wierzchu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jedna odpowiedź na Escape z klawiatury i na przycisk wstecz Androida, bo to jest
    /// to samo pytanie. Dwie osobne rozjechałyby się przy pierwszym dołożonym okienku
    /// — i to na tej platformie, na której nikt tego nie sprawdza codziennie.
    /// </para>
    /// <para>
    /// <b>Przetwarzanie skrzynki jest tu osobnym przypadkiem, nie okienkiem.</b> Chowa
    /// całą nawigację — pasek boczny i dolny — bo ma być drzewkiem decyzyjnym bez
    /// rozpraszania. Na telefonie znaczyło to, że raz wszedłszy, nie dało się z niego
    /// wyjść inaczej niż opróżniając skrzynkę do końca albo zamykając aplikację.
    /// Ekran bez wyjścia jest pułapką niezależnie od tego, jak dobry jest w środku.
    /// </para>
    /// <para>
    /// Fałsz znaczy „nie mam nic do cofnięcia" i oddaje cofnięcie systemowi, czyli
    /// zwykle zamyka aplikację. Odpowiedź zawsze twierdząca zamieniłaby przycisk wstecz
    /// w przycisk, który nic nie robi — a to jest gorsze od zamknięcia aplikacji,
    /// bo po zamknięciu przynajmniej widać, że przycisk działa.
    /// </para>
    /// </remarks>
    private bool Undo()
    {
        if (DataContext is not MainViewModel model)
        {
            return false;
        }

        if (HideKeyboard())
        {
            return true;
        }

        if (model.Detail.IsOpen)
        {
            model.Detail.Close();
        }
        else if (model.Calendar.HasOpened)
        {
            model.Calendar.CloseOpenedCommand.Execute(null);
        }
        else if (model.IsMoreOpen)
        {
            model.CloseMoreCommand.Execute(null);
        }
        else if (model.IsClarify)
        {
            _ = Try("Skrzynka: wyjście z przetwarzania",
                () => model.ShowInboxCommand.ExecuteAsync(null));
        }
        else
        {
            // Nic otwartego nad ekranem — więc cofnięcie znaczy „poprzedni ekran".
            // Pytanie i czynność osobno, bo odpowiedź musi być w tej chwili: Android
            // nie czeka na wczytanie ekranu, tylko na to, czy zajęliśmy się cofnięciem.
            if (!model.CanGoBack)
            {
                return false;
            }

            _ = Try("Nawigacja: cofnięcie", model.BackAsync);
        }

        return true;
    }

    /// <summary>Korzeń okna. Trzymany, bo klawiatura zabiera mu wysokość od dołu.</summary>
    private Grid? _root;

    /// <summary>Czy nasłuch klawiatury jest już podpięty. Podpięcie idzie raz.</summary>
    private bool _keyboardHooked;

    /// <summary>
    /// Podsunięcie treści nad klawiaturę ekranową.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Klawiatura zakrywała dolną część okna, a w tej dolnej części stoi przycisk
    /// „Zapisz". Zapisanie wymagało więc najpierw schowania klawiatury — czyli gestu,
    /// który nie ma nic wspólnego z tym, co się właśnie robi, i którego trzeba się
    /// domyślić. Karta, do której się pisze, ma zostać widoczna razem z przyciskiem,
    /// którym się kończy pisanie.
    /// </para>
    /// <para>
    /// Skrócenie okna, nie przesunięcie. Przesunięcie wypchnęłoby jego górę poza ekran
    /// i nagłówek karty zniknąłby razem z tym, co wpisano wyżej. Zabranie wysokości
    /// sprawia, że to, co i tak się przewija, przewija się w mniejszym oknie — a
    /// przewijanie jest tu właściwą odpowiedzią, bo karta bywa dłuższa niż ekran
    /// także bez klawiatury.
    /// </para>
    /// <para>
    /// Przez <c>InputPane</c>, a nie przez tryb okna Androida: to jest pojęcie Avalonii
    /// i działa tak samo na każdej platformie, która klawiaturę ekranową w ogóle ma.
    /// Na pulpicie zdarzenie nie przychodzi nigdy i wyściółka zostaje zerowa.
    /// </para>
    /// </remarks>
    private void HookKeyboard()
    {
        if (_keyboardHooked)
        {
            return;
        }

        _root ??= this.FindControl<Grid>("Korzen");

        if (TopLevel.GetTopLevel(this)?.InputPane is not { } keyboard || _root is null)
        {
            return;
        }

        _keyboardHooked = true;

        // Margines, nie wyściółka: Panel w Avalonii wyściółki nie ma, a korzeniem okna
        // jest siatka. Skutek jest ten sam — okno kończy się nad klawiaturą.
        //
        // Ze stanu, nie z samej wysokości: przy zamykaniu klawiatura potrafi podać
        // ostatni prostokąt zamiast pustego, a margines zostałby wtedy na zawsze
        // i pod oknem zostałby pas, którego nikt by nie umiał wytłumaczyć.
        keyboard.StateChanged += (_, e) =>
            _root.Margin = new Thickness(
                0, 0, 0, e.NewState == InputPaneState.Open ? e.EndRect.Height : 0);
    }

    /// <summary>
    /// Zamknięcie klawiatury ekranowej, jeśli jest otwarta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pierwsze znaczenie cofnięcia, przed zamykaniem czegokolwiek. Klawiatura zakrywa
    /// dolną połowę ekranu i dopóki stoi, nic pod nią nie jest widoczne — więc zamknięcie
    /// okienka pod nią jest czynnością, której skutku nie widać.
    /// </para>
    /// <para>
    /// Przez odebranie ogniska, a nie przez proszenie systemu: klawiatura na Androidzie
    /// jest odpowiedzią na to, że pisze się w polu tekstowym, więc jedynym trwałym
    /// sposobem jej zamknięcia jest przestać w nim pisać. Poproszona o zniknięcie przy
    /// polu, które nadal ma ognisko, wraca przy pierwszym dotknięciu ekranu.
    /// </para>
    /// </remarks>
    private bool HideKeyboard()
    {
        if (TopLevel.GetTopLevel(this) is not { FocusManager: { } focus })
        {
            return false;
        }

        if (focus.GetFocusedElement() is not TextBox)
        {
            return false;
        }

        focus.ClearFocus();

        return true;
    }

    /// <summary>
    /// Menu podręczne pod prawym przyciskiem — na listach i na blokach kalendarza.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jedno menu na całą aplikację, składane w kodzie, a nie po jednym w każdym
    /// szablonie wiersza. Wiersze różnią się typem — zadanie, wiersz listy, pozycja
    /// oczekiwanych, wybór z „Teraz", blok na siatce — a czynności są te same;
    /// pięć kopii tego samego menu rozjechałoby się przy pierwszej zmianie.
    /// </para>
    /// <para>
    /// W menu są **tylko rzeczy, które działają**. Pozycja, która nic nie robi, uczy
    /// nieufności do całego menu — a nieufne menu przestaje być skrótem.
    /// </para>
    /// </remarks>
    private void NaMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainViewModel model || e.Source is not Control source)
        {
            return;
        }

        // **Na dotyku blok na siatce nie ma menu podręcznego. Ma przeciąganie.**
        //
        // Przytrzymanie i żądanie menu to na dotyku ten sam gest: system zgłasza je po
        // mniej więcej pół sekundy trzymania, czyli tuż po tym, jak nasze przytrzymanie
        // wzięło blok w rękę. Menu kończy chwyt pierwszą linijką niżej, więc jedno
        // wyklucza drugie — i trzeba wybrać. Na siatce wybrane jest przeciąganie.
        //
        // Pytanie idzie o **platformę i o to, co pod palcem**, a nie o blok trzymany
        // w tej chwili. Warunek na trzymany blok był za słaby: żądanie menu potrafi
        // przyjść wtedy, gdy chwyt już się skończył — na przykład po odebraniu
        // wskaźnika przez przewijanie — i menu otwierało się mimo wszystko.
        //
        // Poza siatką nic się nie zmienia: na listach zadań i w drzewie projektów
        // przytrzymanie palcem dalej otwiera menu, bo tam nie ma z czym konkurować,
        // a część jego pozycji nie ma na telefonie innej drogi. Na pulpicie menu
        // zostaje wszędzie — prawy przycisk nie ma nic wspólnego z ciągnięciem lewym.
        //
        // Czego to nie zabiera: pozycje menu bloku są też w jego karcie, a karta
        // otwiera się zwykłym dotknięciem.
        if (Platform.Touch && Block(source) is not null)
        {
            e.Handled = true;
            return;
        }

        // Cokolwiek trzymaliśmy w ręku, menu to kończy. Pozycja „Usuń" wyjmuje blok
        // z siatki, więc puszczenie nie miałoby już dokąd trafić.
        ReleaseBlock();

        if (TaskOf(source) is { } task)
        {
            e.Handled = true;
            _ = Try("Menu: otwarcie", () => ShowMenuAsync(model, source, task));

            return;
        }

        if (Row(source) is { } row)
        {
            e.Handled = true;
            _ = Try("Menu: projekt", () => ShowProjectMenuAsync(model, source, row));

            return;
        }

        // Notatka do wyrzucenia jest zwykle tą, której się nie otwiera — pomyłka przy
        // zakładaniu albo dwa razy to samo. Droga przez otwarcie znaczyła: wejdź, przewiń
        // do przycisków, usuń, wróć. Menu jest tu jednym ruchem, a kafelek pokazuje tytuł
        // i początek tekstu, więc widać, co się usuwa.
        if (NoteAt(source) is { } card)
        {
            e.Handled = true;

            new MenuFlyout
            {
                ItemsSource = new[]
                {
                    Item("Usuń", () => model.Notes.DeleteNoteCommand.ExecuteAsync(card.Note)),
                },
            }.ShowAt(source, showAtPointer: true);

            return;
        }

        // Pasek całodniowy: rytm bez godziny stoi właśnie tam, a nie na siatce.
        // Bez tej gałęzi odwołać dałoby się wyłącznie wystąpienia z godziną — czyli
        // czynność istniałaby zależnie od tego, czy rytm ma porę, a to nie jest różnica,
        // którą ktokolwiek nosi w głowie.
        if (AllDayAt(source) is { RhythmId: { } series, RhythmDate: { } day } entry)
        {
            e.Handled = true;

            new MenuFlyout
            {
                ItemsSource = new[]
                {
                    Item("Pokaż rytm", () =>
                    {
                        model.Calendar.OpenAllDay(entry);
                        return Task.CompletedTask;
                    }),
                    Item(
                        "Odwołaj to wystąpienie",
                        () => model.Calendar.DropOccurrenceAsync(series, day)),
                },
            }.ShowAt(source, showAtPointer: true);

            return;
        }

        if (Block(source) is not { } block)
        {
            return;
        }

        // Blok na siatce niesie sam identyfikator, nie całe zadanie — trzeba je dobrać.
        if (block.TaskId is { } id)
        {
            e.Handled = true;

            _ = Try("Menu: otwarcie", async () =>
            {
                if (await model.FindTaskAsync(id) is { } fromDb)
                {
                    await ShowMenuAsync(model, source, fromDb);
                }
            });

            return;
        }

        e.Handled = true;
        ShowEventMenu(model, source, block);
    }

    /// <summary>
    /// Menu podręczne na wydarzeniu z podłączonego kalendarza.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Do dziś prawy przycisk na wydarzeniu nie robił <b>nic</b>: obsługa rozpoznawała
    /// blok wyłącznie po identyfikatorze zadania, a wydarzenie z Google żadnego nie ma.
    /// Z zewnątrz wyglądało to na menu, które czasem działa — czyli najgorszy rodzaj
    /// działania, bo uczy nie próbować.
    /// </para>
    /// <para>
    /// Dwie pozycje, nie cztery. Kasowanie zostaje na karcie, bo ma tam zaporę
    /// z osobnym przyzwoleniem — skasowanego wydarzenia nie da się odzyskać ani u nas,
    /// ani w Google, a menu podręczne jest miejscem, w które trafia się omsknięciem.
    /// Reszta pól też jest na karcie i to ona jest tu prawdziwą odpowiedzią.
    /// </para>
    /// </remarks>
    private void ShowEventMenu(MainViewModel model, Control source, SlotBox block)
    {
        var calendarId = model.Calendar;
        var rows = new List<MenuItem>();

        var open = new MenuItem
        {
            Header = block.IsAhead ? "Pokaż rytm" : "Otwórz",
        };

        open.Click += (_, _) => calendarId.OpenTaskCommand.Execute(block);
        rows.Add(open);

        // Odwołanie jednego wystąpienia, którego jeszcze nie ma. Rytm zostaje bez zmian
        // — to jest cała różnica wobec „Usuń" na zadaniu, które skasowałoby całą serię.
        if (block is { RhythmId: { } rhythm, RhythmDate: { } occurrence })
        {
            var drop = new MenuItem { Header = "Odwołaj to wystąpienie" };

            drop.Click += (_, _) => _ = Try(
                "Kalendarz: odwołanie wystąpienia",
                () => calendarId.DropOccurrenceAsync(rhythm, occurrence));

            rows.Add(drop);
        }

        if (block.CanComplete)
        {
            var complete = new MenuItem { Header = block.IsDone ? "Zdejmij ptaszek" : "Odhacz" };
            complete.Click += (_, _) => calendarId.ToggleCommand.Execute(block);
            rows.Add(complete);
        }

        new ContextMenu { ItemsSource = rows }.Open(source);
    }

    /// <summary>Kafelek notatki spod wskaźnika.</summary>
    private static NoteCard? NoteAt(Control source) =>
        source.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(k => k.DataContext)
            .OfType<NoteCard>()
            .FirstOrDefault();

    /// <summary>Wpis paska całodniowego spod wskaźnika.</summary>
    private static AllDayBox? AllDayAt(Control source) =>
        source.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(k => k.DataContext)
            .OfType<AllDayBox>()
            .FirstOrDefault();

    /// <summary>Blok siatki spod wskaźnika.</summary>
    private static SlotBox? Block(Control source) =>
        source.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(k => k.DataContext)
            .OfType<SlotBox>()
            .FirstOrDefault();

    private async Task ShowMenuAsync(MainViewModel model, Control source, TaskItem task)
    {
        var today = model.Today;
        var projects = await model.ActiveProjectsAsync();
        var calendars = await model.WritableCalendarsAsync();

        // Wyrażenie kolekcji, nie `new object[] { … }`: rozwinięcie `..` jest częścią
        // tego pierwszego, a w inicjalizatorze tablicy dwie kropki znaczą zakres.
        object[] rows =
        [
            Item("Otwórz szczegół", () => model.OpenTaskAsync(task)),

            // Jedna pozycja, dwa kierunki — zależnie od tego, jak zadanie stoi.
            // Obie naraz kazałyby czytać, która jest teraz właściwa.
            task.State == TaskState.Done
                ? Item("Zdejmij ptaszek", () => model.ReopenTaskAsync(task))
                : Item("Odhacz", () => model.CompleteTaskAsync(task)),

            new Separator(),

            Branch("Ustaw dzień", [
                Item("Dziś", () => model.SetDateAsync(task, today)),
                Item("Jutro", () => model.SetDateAsync(task, today.AddDays(1))),
                Item("Za tydzień", () => model.SetDateAsync(task, today.AddDays(7))),
                Item("Bez dnia", () => model.SetDateAsync(task, null)),
            ]),

            Branch("Waga", [.. PriorityChoice.All.Select(w =>
                Item(w.Label, () => model.SetPriorityAsync(task, w.Value)))]),

            // Oszacowanie i siła są tu, bo bez nich zadanie nigdy nie wypłynie
            // w „Teraz”: ten ekran pyta „ile mam czasu i sił”, a zadanie, które
            // na to nie odpowiada, nie ma jak zostać wybrane. Do dziś dawało się
            // je wpisać tylko przy przetwarzaniu skrzynki albo w szczegółach.
            Branch("Ile zajmie", [.. EstimateChoice.All.Select(m =>
                Item(
                    // „Bez znaczenia" jest odpowiedzią filtra, nie zadania: tu ta
                    // sama wartość znaczy, że oszacowania **nie ma**.
                    m.Value is null ? "bez oszacowania" : m.Label,
                    () => model.SetEstimateAsync(task, m.Value)))]),

            Branch("Ile sił", [.. EnergyChoice.All.Select(e =>
                Item(e.Label, () => model.SetEnergyAsync(task, e.Value)))]),

            Branch("Rytm", [.. RepeatChoice.All.Select(r =>
                Item(r.Label, () => model.SetRecurrenceAsync(task, r.Kind)))]),

            Branch("Projekt", [
                Item("Bez projektu", () => model.SetProjectAsync(task, null)),
                .. projects.Select(p =>
                    Item(p.Outcome, () => model.SetProjectAsync(task, p.Id))),
            ]),

            new Separator(),

            // Udostępnianie pojedynczego zadania, nie całego obszaru: obszar
            // rodzinny mieści i „odebrać dziecko”, i „kupić prezent”, a widzieć
            // je mają różne osoby. Gałąź pokazuje się tylko wtedy, gdy jest dokąd
            // udostępniać — pozycja bez skutku uczy nieufności do całego menu.
            .. Sharing(model, task, calendars),

            Item("Weź na dziś", () => model.FocusTaskAsync(task)),
            Item("Pokaż w kalendarzu", () => model.ShowInCalendarAsync(task)),
            Item("Zamień na notatkę", () => model.ToNoteAsync(task)),
            new Separator(),

            // Zadanie z rytmem niesie całą serię: regułę ma najnowsze wystąpienie,
            // więc „Usuń" kasuje rytm, a nie ten jeden raz. Z nazwy czynności nie da
            // się tego poznać, stąd osobna pozycja — i nazwa mówiąca wprost, czego
            // dotyczy tamta.
            .. Skipping(model, task),

            Item(task.Recurrence is null ? "Usuń" : "Usuń cały rytm",
                () => model.TrashTaskAsync(task)),
        ];

        new MenuFlyout { ItemsSource = rows }.ShowAt(source, showAtPointer: true);
    }

    /// <summary>Wiersz ekranu „Projekty” spod wskaźnika.</summary>
    private static ProjectTreeRow? Row(Control source) =>
        source.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(k => k.DataContext)
            .OfType<ProjectTreeRow>()
            .FirstOrDefault();

    /// <summary>
    /// Dwuklik na obszarze albo projekcie — jego zadania na liście.
    /// </summary>
    /// <remarks>
    /// Na liście, a nie na samym wierszu: wiersz jest szablonem powtórzonym kilkanaście
    /// razy, a podpięcie zdarzenia w szablonie znaczy tyle samo podpięć. Wiersz spod
    /// wskaźnika bierze się z tego, co zdarzenie niesie ze sobą — tak samo, jak przy
    /// menu podręcznym.
    ///
    /// Kwadracik barwy zatrzymuje swoje kliknięcia wcześniej, więc dwuklik w niego
    /// otwiera paletę i nie schodzi przy okazji na zadania.
    /// </remarks>
    private void OnProjectRow(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel model
            || e.Source is not Control source
            || Row(source) is not { } row)
        {
            return;
        }

        e.Handled = true;
        _ = Try("Projekty: zadania wiersza", () => model.ShowScopeTasksAsync(row));
    }

    /// <summary>
    /// Kliknięcie w kwadracik barwy — paleta od razu, bez prawego przycisku.
    /// </summary>
    /// <remarks>
    /// Lewym przyciskiem, bo barwa jest tu główną czynnością wiersza, a nie czymś
    /// schowanym w menu podręcznym. Zdarzenie zatrzymane: bez tego lista zaznaczyłaby
    /// wiersz pod paletą i paleta wyskoczyłaby nad zmienionym zaznaczeniem.
    /// </remarks>
    /// <summary>Dotknięcie wpisu w siatce miesiąca — otwarcie zadania.</summary>
    /// <remarks>
    /// Przez kod, a nie dowiązanie do polecenia. Polecenie mieszka w modelu kalendarza,
    /// a wpis siedzi trzy szablony głębiej, w modelu tygodnia; szukanie polecenia przez
    /// przodka znaczyłoby wyrażenie, które kompiluje się i nie działa — a błędne
    /// dowiązanie nie daje żadnego objawu poza przyciskiem, który nic nie robi.
    /// </remarks>
    private void OnMonthEntry(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not MainViewModel model
            || sender is not Control row
            || row.DataContext is not MonthEntry entry)
        {
            return;
        }

        e.Handled = true;
        model.Calendar.OpenMonthEntry(entry);
    }

    /// <summary>Dotknięcie dnia w siatce miesiąca — zejście na jego siatkę godzinową.</summary>
    /// <remarks>
    /// Na komórce, nie na samym numerze. Numer jest za mały, żeby trafić w niego palcem,
    /// a dotknięcie pustego dnia i tak nie ma innego znaczenia. Wpis przechwytuje swoje
    /// dotknięcie wcześniej, więc kliknięcie w nazwę zadania nie zjeżdża na dzień.
    ///
    /// Na puszczeniu, nie na naciśnięciu. Naciśnięcie jest na dotyku **początkiem
    /// przejechania palcem** — a odkąd przejechanie przewija kalendarz o cały zakres,
    /// reakcja na naciśnięcie znaczyłaby zejście na dzień przy każdej próbie zmiany
    /// miesiąca. Przejechanie jest zresztą przechwytywane wcześniej i nie dochodzi
    /// tutaj wcale; ta zmiana jest po to, żeby nie polegać na tamtej.
    /// </remarks>
    private void OnMonthDay(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not MainViewModel model
            || sender is not Control cell
            || cell.DataContext is not MonthCell day)
        {
            return;
        }

        e.Handled = true;
        _ = Try("Kalendarz: dzień z miesiąca", () => model.Calendar.OpenMonthDayCommand.ExecuteAsync(day));
    }

    private void OnColor(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel model
            || sender is not Control checkbox
            || checkbox.DataContext is not ProjectTreeRow row)
        {
            return;
        }

        e.Handled = true;
        ShowPalette(model, checkbox, row);
    }

    /// <summary>
    /// Wybieraczka barwy: koło, suwaki i pole szesnastkowe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zamiast listy dziewięciu nazw. Lista była wygodna do napisania i zła do
    /// używania: barwa obszaru ma odróżniać go od pozostałych **jednym spojrzeniem**,
    /// a przy siedmiu obszarach dziewięć propozycji znaczy, że dobiera się je już nie
    /// do siebie, tylko do tego, co zostało wolne. Do tego nazwy nie mówią, jak coś
    /// wygląda — „pomarańczowy" trzeba było wybrać, żeby zobaczyć.
    /// </para>
    /// <para>
    /// Zapis dopiero na „Ustaw", nie przy każdym ruchu myszy po kole: każda zmiana to
    /// zapis do bazy i przerysowanie kalendarza, a przeciągnięcie po widmie daje ich
    /// kilkaset. Do tego barwa wybrana przypadkiem po drodze nie ma zostawiać śladu
    /// w dzienniku zmian, z którym potem scala się drugie urządzenie.
    /// </para>
    /// </remarks>
    private void ShowPalette(MainViewModel model, Control source, ProjectTreeRow row)
    {
        var kolo = new ColorView
        {
            Color = Color.TryParse(row.Color ?? string.Empty, out var current)
                ? current
                : Colors.SlateGray,
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            Width = 300,
        };

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        var set = new Button { Content = "Ustaw", Padding = new Thickness(14, 6) };
        var clear = new Button { Content = "Bez barwy", Padding = new Thickness(14, 6) };

        set.Click += (_, _) =>
        {
            flyout.Hide();
            var c = kolo.Color;
            _ = Try(
                "Barwa: ustawienie",
                () => model.SetRowColorAsync(row, $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
        };

        clear.Click += (_, _) =>
        {
            flyout.Hide();
            _ = Try("Barwa: zdjęcie", () => model.SetRowColorAsync(row, null));
        };

        // Barwy przygotowane zostają nad kołem, jednym kliknięciem. Koło jest po to,
        // żeby dało się wyjść poza listę, a nie po to, żeby za każdym razem trzeba
        // było trafiać myszą w odcień, który i tak jest na liście.
        var quick = new WrapPanel();

        foreach (var color in ColorChoice.All.Where(b => b.Value is not null))
        {
            var choice = color;
            // Barwa na ramce w środku, nie na tle przycisku: tło przycisku motyw
            // przemalowuje przy najechaniu, a kwadracik, który zmienia kolor pod
            // wskaźnikiem, przestaje pokazywać to, co ma pokazywać.
            var checkbox = new Button
            {
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(7),
                [ToolTip.TipProperty] = choice.Label,
                Content = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.Parse(choice.Value!)),
                },
            };

            checkbox.Click += (_, _) =>
            {
                flyout.Hide();
                _ = Try("Barwa: ustawienie", () => model.SetRowColorAsync(row, choice.Value));
            };

            quick.Children.Add(checkbox);
        }

        flyout.Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                quick,
                kolo,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Children = { set, clear },
                },
            },
        };

        flyout.ShowAt(source);
    }

    /// <summary>
    /// Menu wiersza struktury — to samo dla obszaru i dla projektu.
    /// </summary>
    /// <remarks>
    /// Jedno menu na oba poziomy, bo czynności są te same: nazwa, barwa, założenie
    /// czegoś pod spodem, usunięcie. Do dziś obszary i projekty mieszkały na dwóch
    /// ekranach i każdy dawał co innego — tu barwę i usunięcie projektu, tam zakładanie
    /// obszaru, a nazwę wyłącznie tam. Cztery osobne braki, jedna przyczyna.
    ///
    /// Powód odmowy sprawdzany przed pokazaniem menu: pozycja, która po kliknięciu nic
    /// nie robi, uczy nieufności do całego menu.
    /// </remarks>
    private async Task ShowProjectMenuAsync(MainViewModel model, Control source, ProjectTreeRow row)
    {
        var blocker = await model.WhyCannotDeleteRowAsync(row);

        var rows = new List<object>
        {
            Item("Zmień nazwę…", () =>
            {
                ShowNameField(
                    source, row.Label, "Zapisz",
                    name => model.RenameRowAsync(row, name));

                return Task.CompletedTask;
            }),
            Item(row.AddLabel, () =>
            {
                ShowNameField(
                    source, string.Empty, "Załóż",
                    name => model.AddProjectAsync(row, name));

                return Task.CompletedTask;
            }),
            Item("Barwa…", () =>
            {
                ShowPalette(model, source, row);
                return Task.CompletedTask;
            }),
        };

        // Kalendarz tylko przy obszarze: projekt go nie ma i mieć nie powinien. Obszar
        // jest podziałem odpowiedzialności, a kalendarz Google jest tym samym podziałem
        // widzianym z zewnątrz — projekt jest o poziom za drobny, żeby zakładać dla
        // niego osobny kalendarz.
        if (row.IsArea && await CalendarForAreaAsync(model, row) is { } calendars)
        {
            rows.Add(calendars);
        }

        rows.Add(new Separator());

        rows.Add(blocker is null
            ? Item(row.IsArea ? "Usuń obszar" : "Usuń projekt",
                () => model.DeleteRowAsync(row))
            : new MenuItem { Header = blocker, IsEnabled = false });

        new MenuFlyout { ItemsSource = rows }.ShowAt(source, showAtPointer: true);
    }

    /// <summary>
    /// Gałąź „Kalendarz Google" przy obszarze — z ptaszkiem przy tym, który jest teraz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Obszar wskazujący kalendarz znaczy dwie rzeczy naraz: zadania tego obszaru
    /// lądują tam same, a wydarzenia stamtąd należą do tego obszaru. Jedno przypisanie
    /// zamiast ustawiania kalendarza przy każdym zadaniu z osobna.
    /// </para>
    /// <para>
    /// Przy obszarze, a nie w ustawieniach kalendarzy: wybiera się to patrząc na listę
    /// obszarów. Kalendarz jest tu cechą obszaru — „gdzie to widać na zewnątrz" — a nie
    /// osobną rzeczą do skonfigurowania.
    /// </para>
    /// <para>
    /// Bez podłączonych kalendarzy oddaje pusto, a nie gałąź z pustą listą: pozycja
    /// menu, która nic nie otwiera, jest gorsza od jej braku, bo wygląda na zepsutą.
    /// </para>
    /// </remarks>
    private async Task<MenuItem?> CalendarForAreaAsync(MainViewModel model, ProjectTreeRow row)
    {
        var calendars = await model.WritableCalendarsAsync();

        if (calendars.Count == 0)
        {
            return null;
        }

        var now = await model.AreaCalendarAsync(row.Id);

        var rows = new List<MenuItem>
        {
            Item(now is null ? "✓ żaden" : "żaden",
                () => model.SetAreaCalendarAsync(row, null)),
        };

        rows.AddRange(calendars.Select(k => Item(
            now == k.Id ? $"✓ {k.Name}" : k.Name,
            () => model.SetAreaCalendarAsync(row, k.Id))));

        return Branch("Kalendarz Google", rows);
    }

    /// <summary>
    /// Małe okienko z jednym polem tekstowym przy wierszu.
    /// </summary>
    /// <remarks>
    /// Pole wprost na liście byłoby czwartą rzeczą w wierszu, który już niesie nazwę,
    /// kwadracik barwy i liczby — a nazwę zmienia się raz na parę miesięcy. Enter
    /// zapisuje, bo po wpisaniu ręka i tak tam idzie.
    /// </remarks>
    private void ShowNameField(
        Control source, string initial, string button, Func<string, Task> work)
    {
        var pole = new TextBox { Text = initial, Width = 260 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        void Save()
        {
            var name = pole.Text ?? string.Empty;
            flyout.Hide();
            _ = Try($"Struktura: {button}", () => work(name));
        }

        pole.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                Save();
            }
        };

        var save = new Button { Content = button, Padding = new Thickness(14, 6) };
        save.Click += (_, _) => Save();

        flyout.Content = new StackPanel { Spacing = 8, Children = { pole, save } };
        flyout.ShowAt(source);

        pole.Focus();
        pole.SelectAll();
    }

    /// <summary>
    /// Pozycje menu od kalendarza — albo żadne, gdy nie ma dokąd przenosić.
    /// </summary>
    /// <remarks>
    /// Zadanie stoi w jednym kalendarzu naraz, więc lista pokazuje **pozostałe**,
    /// a nie wszystkie: „przenieś tam, gdzie już jest" nie jest czynnością. Zadanie
    /// wyniesione poza główny dostaje dodatkowo powrót — bo to jest jedyny sposób,
    /// żeby zniknęło z cudzego widoku, nie znikając ze swojego.
    /// </remarks>
    /// <summary>Pominięcie bieżącego wystąpienia — tylko przy zadaniu z rytmem.</summary>
    /// <remarks>
    /// Pusto tam, gdzie rytmu nie ma: pozycja bez skutku uczy nieufności do całego menu.
    /// </remarks>
    private IReadOnlyList<object> Skipping(MainViewModel model, TaskItem task) =>
        task.Recurrence is null
            ? []
            : [Item("Pomiń to wystąpienie", () => model.SkipOccurrenceAsync(task))];

    private IReadOnlyList<object> Sharing(
        MainViewModel model, TaskItem task, IReadOnlyList<CalendarSource> calendars)
    {
        var whereIs = task.SharedCalendarId;
        var primary = model.MainCalendarId;

        var rest = calendars.Where(k => k.Id != whereIs).ToList();
        var rows = new List<object>();

        if (rest.Count > 0)
        {
            rows.Add(Branch("Przenieś do kalendarza", [.. rest.Select(k =>
                Item(k.Name, () => model.ShareTaskAsync(task, k.Id)))]));
        }

        if (whereIs is not null && primary is not null && whereIs != primary)
        {
            rows.Add(Item(
                "Z powrotem na kalendarz główny", () => model.UnshareTaskAsync(task)));
        }

        return rows;
    }

    /// <summary>Pozycja menu. Woła metodę wprost — wyjątek ma dokąd trafić.</summary>
    private MenuItem Item(string label, Func<Task> work)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => _ = Try($"Menu: {label}", work);

        return item;
    }

    private static MenuItem Branch(string label, IReadOnlyList<MenuItem> rows) =>
        new() { Header = label, ItemsSource = rows };

    /// <summary>Zadanie spod wskaźnika — niezależnie od tego, czym jest wiersz.</summary>
    private static TaskItem? TaskOf(Control source)
    {
        foreach (var ancestor in source.GetSelfAndVisualAncestors().OfType<Control>())
        {
            var found = ancestor.DataContext switch
            {
                TaskItem directly => directly,
                TaskRow row => row.Task,
                WaitingItem pending => pending.Task,
                NowPick choice => choice.Task,
                _ => null,
            };

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Dwuklik na dowolnej liście otwiera szczegół zadania.
    /// </summary>
    /// <remarks>
    /// Jedna obsługa na wszystkie listy, bo wiersze różnią się typem, a nie
    /// zachowaniem. Dotąd część widoków — skrzynka, oczekiwane, kiedyś, archiwum —
    /// nie miała **żadnej** drogi do edycji: zadanie dało się tam zobaczyć i nic
    /// więcej. Lista, z której nie da się otworzyć tego, co się widzi, jest ślepa.
    /// </remarks>
    private void OpenFromList(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        // Z zaznaczenia listy, a gdy go nie ma — z wiersza pod wskaźnikiem. Ekran
        // „Teraz" nie jest listą do zaznaczania, tylko odpowiedzią na pytanie, więc
        // jego wiersze nie mają zaznaczenia w ogóle.
        var task = (sender as ListBox)?.SelectedItem switch
        {
            TaskItem directly => directly,
            TaskRow row => row.Task,
            WaitingItem pending => pending.Task,
            NowPick choice => choice.Task,
            _ => e.Source is Control source ? TaskOf(source) : null,
        };

        if (task is not null)
        {
            _ = Try("Lista: otwarcie zadania", () => model.Detail.LoadAsync(task));
        }
    }

    /// <summary>Dwuklik na notatce otwiera ją do czytania i poprawiania.</summary>
    /// <summary>
    /// Ile miejsca dostały kafelki notatek.
    /// </summary>
    /// <remarks>
    /// Szerokość kafelka liczy model ekranu, bo to on wie, ile ich ma — okno zna tylko
    /// prostokąt. Ta sama droga co przy kolumnach kalendarza i z tego samego powodu:
    /// wyliczona w XAML-u byłaby stała, a stała wygląda dobrze na jednej szerokości okna.
    /// </remarks>
    private void OnNotesWidth(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.Notes.SetAvailableWidth(e.NewSize.Width);
        }
    }

    /// <summary>Kliknięcie w pasek całodniowy — zadanie na cały dzień też ma szczegół.</summary>
    private void OpenAllDay(object? sender, RoutedEventArgs e)
    {
        if (sender is Control button && button.Tag is AllDayBox entry)
        {
            _calendar?.OpenAllDay(entry);
        }
    }

    private void OnDetail(string what, Func<TaskDetailViewModel, Task> work)
    {
        if (_detail is not { } model)
        {
            return;
        }

        _ = Try(what, () => work(model));
    }

    private async Task Try(string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (DataContext is MainViewModel model)
            {
                await model.Journal.RecordAsync(
                    what, "nie udało się", $"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    private CalendarViewModel? _calendar;

    /// <summary>Gdzie i czym zaczęło się dotknięcie pustej siatki.</summary>
    private (DateOnly Day, Point From, IPointer Pointer)? _touchedDay;

    /// <summary>
    /// Dotknięcie pustej siatki zakłada nową rzecz na tej godzinie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Obsługiwane na warstwie linii godzin, nie na blokach: bloki są przyciskami
    /// i zjadają kliknięcie same, więc klik w zajęte miejsce nie trafia tutaj i nie
    /// zakłada niczego pod spodem. Dzień bierze się ze znacznika ustawionego w XAML-u,
    /// bo warstwa linii nie zna kolumny, na której leży.
    /// </para>
    /// <para>
    /// <b>Naciśnięcie zapamiętuje, zakłada dopiero puszczenie.</b> Dotąd wystarczyło
    /// samo naciśnięcie i na myszy było to w porządku — klik to naciśnięcie i puszczenie
    /// w tym samym miejscu. Na dotyku naciśnięcie jest **początkiem przewijania**, więc
    /// próba przesunięcia kalendarza palcem zakładała zadanie na godzinie, w którą
    /// akurat trafił palec. Ręka, która przejechała dalej niż próg, należy do
    /// przewijania i nie zakłada niczego.
    /// </para>
    /// </remarks>
    private void NewThingOnGrid(object? sender, PointerPressedEventArgs e)
    {
        _touchedDay = null;

        if (sender is not Control layer || layer.Tag is not DateOnly day)
        {
            return;
        }

        // Tylko lewy przycisk. Prawy i środkowy też dają PointerPressed, a zakładanie
        // zadania menu podręcznym byłoby niespodzianką.
        if (!e.GetCurrentPoint(layer).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _touchedDay = (day, e.GetPosition(layer), e.Pointer);
    }

    private void GridReleased(object? sender, PointerReleasedEventArgs e)
    {
        var start = _touchedDay;
        _touchedDay = null;

        if (start is not { } touch
            || sender is not Control layer
            || !ReferenceEquals(touch.Pointer, e.Pointer))
        {
            return;
        }

        var end = e.GetPosition(layer);

        if (Math.Abs(end.X - touch.From.X) > DragThreshold
            || Math.Abs(end.Y - touch.From.Y) > DragThreshold)
        {
            return;
        }

        // Z miejsca naciśnięcia, nie puszczenia: godzina ma być tą, w którą się trafiło,
        // a nie tą, na którą palec zjechał o trzy punkty.
        _calendar?.NewAt(touch.Day, touch.From.Y);
    }

    /// <summary>Przewijanie przejęło wskaźnik — to nie było dotknięcie siatki.</summary>
    private void GridAbandoned(object? sender, PointerCaptureLostEventArgs e) =>
        _touchedDay = null;

    /// <summary>Zapas na suwak i odstęp między kolumnami.</summary>
    private const double Capacity = 14;

    private void OnWidthChange(object? sender, SizeChangedEventArgs e)
    {
        SetWidth(e.NewSize.Width);
        Remeasure();
    }

    /// <summary>
    /// Przeliczenie pasków przewijanych w bok po zmianie szerokości okna.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Objaw: po zmaksymalizowaniu okna nazwy dni i pasek całodniowy urywały się
    /// w połowie czwartej kolumny, a siatka pod nimi miała komplet siedmiu. Kolumny
    /// nagłówków miały przy tym **nową**, szeroką miarę — urywały się dokładnie tam,
    /// gdzie kończyła się szerokość okna sprzed zmaksymalizowania.
    /// </para>
    /// <para>
    /// Czyli: zawartość paska przemierzyła się na nowo, ale jej miejsce zostało stare.
    /// Siedem wąskich kolumn zajmowało tyle, co trzy szerokie — i do tylu właśnie była
    /// przycięta. Siatka tego nie miała, bo jej pasek przewijania jest wyliczany
    /// („Auto"), a paski nagłówków i podglądów mają go schowanego na stałe; to dwie
    /// różne drogi przez układanie i tylko jedna z nich dostawała nową szerokość.
    /// </para>
    /// <para>
    /// Unieważnienie zawartości, a nie samego paska: to zawartość ma złe miejsce.
    /// Miara idzie w górę sama, więc jedno wywołanie pociąga za sobą oba przebiegi —
    /// i dzieje się to wyłącznie przy zmianie rozmiaru okna, czyli kilka razy na dzień.
    /// </para>
    /// <para>
    /// Podglądy sąsiednich zakresów tą samą drogą. Są niewidoczne poza przejechaniem
    /// palcem, więc ich przycięcia nie widać, dopóki nie przejedzie się po
    /// zmaksymalizowaniu okna — a wtedy sąsiedni tydzień pokazałby się obcięty.
    /// </para>
    /// </remarks>
    private void Remeasure()
    {
        Afresh(_headings);
        Afresh(_gridBefore);
        Afresh(_gridAfter);

        static void Afresh(ScrollViewer? strip)
        {
            if (strip?.Content is not Control inside)
            {
                return;
            }

            inside.InvalidateMeasure();
            inside.InvalidateArrange();
        }
    }

    /// <summary>
    /// Przesuwanie kreski bieżącej godziny.
    /// </summary>
    /// <remarks>
    /// Kreska liczy się przy składaniu siatki, więc przy otwartej aplikacji stała
    /// tam, gdzie wypadła przy wejściu na ekran — po kilku godzinach pokazywała
    /// godzinę sprzed kilku godzin i wyglądała jak błąd w strefie czasowej.
    /// Co minutę, bo częściej nie ma czego pokazywać: minuta to jeden punkt siatki.
    /// </remarks>
    private DispatcherTimer? _timer;

    /// <summary>Pasek z nazwami dni. Stoi nad siatką i jedzie z nią tylko w poziomie.</summary>
    private ScrollViewer? _headings;

    private void WireClock(MainViewModel model)
    {
        _timer ??= new DispatcherTimer(
            TimeSpan.FromMinutes(1),
            DispatcherPriority.Background,
            (_, _) => Run(model));

        _timer.Start();

        // Minutnik chodzi wyłącznie wtedy, gdy ktoś patrzy. Zob. Uspienie: w tle ta sama
        // praca jest zbędna, bo robią ją Budzik i pracownik synchronizacji, i ryzykowna,
        // bo schowaną aplikację system zamraża — także w środku zapytania do bazy albo
        // odczytu z sieci. Ustawienie, a nie dopisanie się do zdarzenia: okno bywa
        // składane po raz drugi, a dwa podpięcia znaczyłyby dwa przebiegi na minutę.
        Sleep.OnSleep = () => _timer?.Stop();

        Sleep.OnWake = () =>
        {
            _timer?.Start();

            // Od razu, nie za minutę. Wracając po godzinie zastaje się kreskę bieżącej
            // godziny sprzed godziny i listę przypomnień sprzed godziny — czyli ekran,
            // który wygląda na zepsuty, choć czeka tylko na najbliższy przebieg.
            Run(model);
        };

        HookWindowReturn(model);
    }

    /// <summary>Jeden przebieg minutnika. Osobno, bo powrót na wierzch robi to samo.</summary>
    private void Run(MainViewModel model)
    {
        model.Calendar.Tick();

        // Przypomnienia razem z kreską „teraz": jedno i drugie jest odpowiedzią
        // na upływ czasu, a drugi minutnik na tę samą minutę byłby drugim
        // miejscem do zatrzymania i do zapomnienia o zatrzymaniu.
        _ = Try("Przypomnienia: sprawdzenie", model.CheckRemindersAsync);
    }

    /// <summary>Czy powrót do okna jest już podsłuchiwany. Podpięcie idzie raz.</summary>
    private bool _returnHooked;

    /// <summary>
    /// Powrót do okna sięga na Dysk od razu, bez czekania na przerwę.
    /// </summary>
    /// <remarks>
    /// <para>
    /// To najlepszy moment, jaki ta aplikacja ma: dokładnie wtedy ktoś zaczyna patrzeć
    /// na listę i dokładnie wtedy różnica między telefonem a komputerem jest widoczna.
    /// Przerwa zostaje dla okna, przy którym się siedzi — tam nikt nie wraca, bo nikt
    /// nie wychodził.
    /// </para>
    /// <para>
    /// Na oknie, nie na cyklu życia aplikacji: pulpit jest tym miejscem, gdzie zadanie
    /// zapisane na telefonie ma się pojawić, a tam „wrócenie" znaczy przełączenie się
    /// na okno i nic więcej. Android ma na to robotę w tle, więc nie potrzebuje tego
    /// haczyka, żeby nadrobić.
    /// </para>
    /// </remarks>
    private void HookWindowReturn(MainViewModel model)
    {
        if (_returnHooked || TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        _returnHooked = true;

        window.Activated += (_, _) =>
            _ = Try("Synchronizacja po powrocie", model.SyncAfterReturnAsync);
    }

    /// <summary>
    /// Co jedzie za siatką: nagłówki w poziomie, godziny i sąsiedzi w pionie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nagłówki pionowo <b>stoją</b> — o to właśnie chodzi, bo inaczej uciekały do góry
    /// przy pierwszym obrocie kółka i po kilku godzinach dnia nie było wiadomo, na którą
    /// kolumnę się patrzy.
    /// </para>
    /// <para>
    /// Godziny odwrotnie: pionowo jadą, w poziomie stoją. Stoją, bo nie należą do
    /// tygodnia, tylko do doby, i przejechanie palcem nie ma prawa ich zabierać —
    /// a stoją przez to, że są poza warstwą, która się rusza. Cena za to jest tutaj:
    /// ich pion trzeba dowozić ręcznie, bo nie dzielą już z siatką widoku przewijanego.
    /// </para>
    /// <para>
    /// Sąsiedzi tak samo, i to jest teraz <b>konieczne</b>, a nie ozdobne: podpisy godzin
    /// stoją nieruchomo, więc sąsiad przewinięty na inną porę pokazywałby swoje bloki
    /// przy cudzych godzinach. Dopóki godziny jechały razem z nim, rozjazd był niewidoczny.
    /// </para>
    /// </remarks>
    private void OnGridScroll(object? sender, ScrollChangedEventArgs e)
    {
        if (_grid is null)
        {
            return;
        }

        if (_headings is not null)
        {
            _headings.Offset = new Vector(_grid.Offset.X, 0);
        }

        var down = _grid.Offset.Y;

        Follow(_hours, down);
        Follow(_gridBefore, down);
        Follow(_gridAfter, down);

        // Bez zmiany, gdy już tam stoi: ustawienie przesunięcia zgłasza kolejną zmianę
        // przewijania, a ta wraca tutaj.
        static void Follow(ScrollViewer? view, double down)
        {
            if (view is not null && Math.Abs(view.Offset.Y - down) > 0.5)
            {
                view.Offset = new Vector(0, down);
            }
        }
    }

    /// <remarks>
    /// Bez odejmowania kolumny godzin: stoi ona poza siatką, więc szerokość, którą
    /// siatka zgłasza, już jej nie zawiera. Odjęta drugi raz zostawiała po prawej
    /// pustą kolumnę szeroką dokładnie na godziny.
    /// </remarks>
    private void SetWidth(double whole)
    {
        if (whole > 0)
        {
            _calendar?.SetAvailableWidth(whole - Capacity);
        }
    }

    private void OnScrollRequest(double y)
    {
        _target = y;
        Scroll();
    }

    private void OnLayout(object? sender, EventArgs e) => Scroll();

    /// <summary>
    /// Ustawienie przesunięcia, gdy jest już czym przesuwać.
    /// </summary>
    /// <remarks>
    /// Prośba przychodzi po wczytaniu danych, a więc **przed** złożeniem układu:
    /// ekran kalendarza bywa w tym momencie dopiero odsłaniany i siatka ma zerową
    /// wysokość. Przesunięcie zostałoby wtedy przycięte do zera bez śladu, że
    /// cokolwiek się nie udało. Dlatego prośba czeka na pierwszy układ, w którym
    /// siatka ma już rozmiar, i dopiero wtedy jest realizowana — raz.
    /// </remarks>
    private void Scroll()
    {
        if (_target is not { } target || _grid is null || _grid.Extent.Height <= 0)
        {
            return;
        }

        var slack = Math.Max(0, _grid.Extent.Height - _grid.Viewport.Height);

        _grid.Offset = new Vector(_grid.Offset.X, Math.Clamp(target, 0, slack));
        _target = null;
    }

    /// <summary>
    /// Wybór pliku kopii. Robi go okno, nie model widoku.
    /// </summary>
    /// <remarks>
    /// Na Androidzie „zapisz plik" to dialog systemowy podpięty do bieżącego ekranu,
    /// a wynikiem jest uchwyt do treści, nie ścieżka na dysku — ścieżki w rozumieniu
    /// pulpitu tam po prostu nie ma. Dlatego model widoku dostaje gotowy strumień
    /// i wie, **co** zapisać, a nie **gdzie**.
    /// </remarks>
    private void WirePicker()
    {
        if (DataContext is not MainViewModel model || TopLevel.GetTopLevel(this) is not { } window)
        {
            return;
        }

        // Pierwsze rozstrzygnięcie układu: zdarzenie rozmiaru potrafi wypaść przed
        // podstawieniem modelu, a wtedy nie miałby go kto ustawić.
        model.IsNarrow = Bounds.Width > 0 && Bounds.Width < NarrowView;

        WireCalendar(model);
        WireClock(model);
        HookKeyboard();

        model.Settings.SaveRequested = async name =>
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Kopia zapasowa Marshala",
                SuggestedFileName = name,
                DefaultExtension = "json",
                FileTypeChoices = [Json],
            });

            return file is null ? null : await file.OpenWriteAsync();
        };

        model.Settings.OpenRequested = async () =>
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Wczytaj kopię Marshala",
                AllowMultiple = false,
                FileTypeFilter = [Json],
            });

            return files.Count == 0 ? null : await files[0].OpenReadAsync();
        };
    }

    private static FilePickerFileType Json => new("Kopia Marshala")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };
}
