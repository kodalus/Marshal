using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
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
using Marshal.Domain.Notes;
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
    private const double WidokWaski = 720;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            WirePicker();
            OdsloniecieGotowego();
        };

        // Klawisze łapane w drodze w dół: inaczej kontrolka pod kursorem zjada
        // zdarzenie, zanim okno zdąży cokolwiek z nim zrobić.
        //
        // Kółka **nie** łapiemy. Przekierowanie go do okna szczegółu odbierało obrót
        // rozwiniętej liście godzin, czyli psuło wybieranie godziny — a zamknięte pole
        // daty ani godziny kółka nie zjada, więc przewijanie okna działa samo.
        AddHandler(KeyDownEvent, NaKlawiszu, RoutingStrategies.Tunnel);
        AddHandler(ContextRequestedEvent, NaMenu, RoutingStrategies.Bubble);

        // Cofnięcie systemowe — na Androidzie przycisk albo gest wstecz. Ta sama
        // odpowiedź co Escape, bo to jest to samo pytanie: „zamknij to, co na wierzchu".
        Wstecz.Obsluga = Cofnij;

        // **Także na oknie.** Klawisz idzie drogą od okna do tego, co ma skupienie —
        // a gdy skupienia nie ma nic, droga kończy się na oknie i ten widok nie leży
        // na niej wcale. Tak jest zaraz po otwarciu karty wydarzenia kliknięciem
        // w siatkę: Escape nie zamykał jej, bo ta obsługa nigdy się nie odzywała.
        AttachedToVisualTree += (_, _) =>
            TopLevel.GetTopLevel(this)?.AddHandler(
                KeyDownEvent, NaKlawiszu, RoutingStrategies.Tunnel);

        // Układ dobierany z faktycznej szerokości, nie z platformy: obrót telefonu
        // i zwężenie okna to ta sama zmiana.
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.IsNarrow = e.NewSize.Width < WidokWaski;
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
    private void OdsloniecieGotowego()
    {
        if (this.FindControl<Panel>("ZaslonaStartu") is { } zaslona)
        {
            zaslona.IsVisible = DataContext is null;
        }
    }

    private ScrollViewer? _siatka;

    /// <summary>Przesunięcie, które czeka na zmierzenie siatki. Null, gdy nic nie czeka.</summary>
    private double? _docelowe;

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
        _siatka ??= this.FindControl<ScrollViewer>("SiatkaKalendarza");
        _naglowki ??= this.FindControl<ScrollViewer>("NaglowkiDni");
        _kalendarz = model.Calendar;
        _szczegol = model.Detail;

        model.Calendar.ScrollRequested -= NaProsbeOPrzewiniecie;
        model.Calendar.ScrollRequested += NaProsbeOPrzewiniecie;

        if (_siatka is not null)
        {
            _siatka.LayoutUpdated -= NaUkladzie;
            _siatka.LayoutUpdated += NaUkladzie;

            // Nagłówki jadą w poziomie za siatką. Pionowo stoją — o to właśnie chodzi,
            // bo inaczej uciekały do góry przy pierwszym obrocie kółka i po kilku
            // godzinach dnia nie było wiadomo, na którą kolumnę się patrzy.
            _siatka.ScrollChanged -= NaPrzewinieciuSiatki;
            _siatka.ScrollChanged += NaPrzewinieciuSiatki;
            _siatka.SizeChanged -= NaZmianieSzerokosci;
            _siatka.SizeChanged += NaZmianieSzerokosci;

            // Pierwsze podanie szerokości: zdarzenie rozmiaru potrafi wypaść przed
            // podstawieniem modelu, a wtedy siatka zostałaby na szerokości zapasowej.
            Szerokosc(_siatka.Bounds.Width);
        }

        // To samo dla wysokości miesiąca. Siatka tygodni zgłasza się przy wczytaniu,
        // a to bywa **przed** podstawieniem modelu — wtedy jej meldunek trafiał donikąd
        // i komórka zostawała przy pojemności zapasowej do pierwszej zmiany rozmiaru
        // okna. Na telefonie, gdzie okna się nie zmienia, znaczyło to „nigdy".
        if (_siatkaMiesiaca is { Bounds.Height: > 0 } tygodnie)
        {
            _kalendarz.SetMonthHeight(tygodnie.Bounds.Height);
        }
    }

    /// <summary>Model szczegółu zadania. Podstawiany razem z resztą, przy zmianie kontekstu.</summary>
    private TaskDetailViewModel? _szczegol;

    /// <summary>
    /// Przyciski okna szczegółu wołane wprost.
    /// </summary>
    /// <remarks>
    /// Przez polecenia kliknięcie w „Zapisz" kończyło się niczym i nie zostawiało śladu
    /// nawet w pierwszej linijce zapisu — czyli do metody w ogóle nie docierało.
    /// Tu wyjątek też ma dokąd trafić: bez tego byłaby to ta sama pułapka, tylko
    /// przeniesiona o warstwę niżej.
    /// </remarks>
    private void ZapiszZadanie(object? nadawca, RoutedEventArgs e) =>
        Zadanie("Zadanie: zapis z okna", m => m.SaveAsync());

    private void OdhaczZadanie(object? nadawca, RoutedEventArgs e) =>
        Zadanie("Zadanie: odhaczenie z okna", m => m.CompleteAsync());

    private void UsunZadanie(object? nadawca, RoutedEventArgs e) =>
        Zadanie("Zadanie: do kosza z okna", m => m.TrashAsync());

    private void ZamknijZadanie(object? nadawca, RoutedEventArgs e) =>
        Zadanie("Zadanie: zamknięcie okna", m =>
        {
            m.Close();
            return Task.CompletedTask;
        });

    /// <summary>Enter w nazwie zadania zapisuje — tak jak w każdym polu z jedną linijką.</summary>
    private void NazwaKlawisz(object? nadawca, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            Zadanie("Zadanie: zapis z klawisza", m => m.SaveAsync());
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
    private void ObszarKalendarzaGotowy(object? nadawca, RoutedEventArgs e)
    {
        if (nadawca is not Control obszar)
        {
            return;
        }

        // Loaded potrafi przyjść po każdym powrocie na ekran, a dwa rozpoznawacze
        // na jednym panelu liczyłyby ten sam ruch dwa razy.
        if (obszar.GestureRecognizers.Count == 0)
        {
            obszar.GestureRecognizers.Add(new ScrollGestureRecognizer
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
        obszar.AddHandler(
            Gestures.ScrollGestureEvent, ObszarGest, RoutingStrategies.Bubble, handledEventsToo: true);

        obszar.AddHandler(
            Gestures.ScrollGestureEndedEvent, ObszarGestSkonczony,
            RoutingStrategies.Bubble, handledEventsToo: true);

        obszar.AddHandler(PointerPressedEvent, ObszarNacisniety, RoutingStrategies.Tunnel);
        obszar.AddHandler(PointerMovedEvent, ObszarRuch, RoutingStrategies.Tunnel);
        obszar.AddHandler(PointerReleasedEvent, ObszarPuszczony, RoutingStrategies.Tunnel);

        _obszarKalendarza = obszar;
        _podgladPrzed = this.FindControl<Panel>("PodgladPrzed");
        _podgladPo = this.FindControl<Panel>("PodgladPo");
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
    private void SiatkaMiesiacaGotowa(object? nadawca, RoutedEventArgs e)
    {
        if (nadawca is not Control siatka)
        {
            return;
        }

        _siatkaMiesiaca = siatka;

        siatka.SizeChanged -= NaZmianieWysokosciMiesiaca;
        siatka.SizeChanged += NaZmianieWysokosciMiesiaca;

        _kalendarz?.SetMonthHeight(siatka.Bounds.Height);
    }

    /// <summary>Siatka tygodni miesiąca. Trzymana, żeby podać jej wysokość po modelu.</summary>
    private Control? _siatkaMiesiaca;

    private void NaZmianieWysokosciMiesiaca(object? nadawca, SizeChangedEventArgs e) =>
        _kalendarz?.SetMonthHeight(e.NewSize.Height);

    /// <summary>Podglądy sąsiednich zakresów. Widoczne wyłącznie w trakcie przejechania.</summary>
    private Panel? _podgladPrzed;

    private Panel? _podgladPo;

    /// <summary>Żeby brak warstw trafił do dziennika raz, a nie przy każdym ruchu palca.</summary>
    private bool _brakWarstwZapisany;

    /// <summary>Panel z siatkami. Trzymany do przesunięcia przy zmianie zakresu.</summary>
    private Control? _obszarKalendarza;

    /// <summary>Ile trzeba przejechać w bok, żeby to było przejechanie, a nie przewijanie.</summary>
    /// <remarks>
    /// Sześćdziesiąt punktów to około jednej szóstej szerokości telefonu: za dużo, żeby
    /// wyszło przy pionowym przewijaniu, za mało, żeby trzeba było brać rozmach.
    /// </remarks>
    private const double ProgPrzejechania = 60;

    /// <summary>Od ilu punktów w bok siatka zaczyna iść za palcem.</summary>
    /// <remarks>
    /// Dwanaście, bo tyle mieści się w drgnięciu ręki przy przewijaniu w pionie.
    /// Niżej siatka drgałaby w bok przy każdym ruchu po godzinach.
    /// </remarks>
    private const double ProgSledzenia = 12;

    /// <summary>Zsumowane przesunięcie bieżącego gestu.</summary>
    private Vector _gest;

    /// <summary>Czy ten gest już przeskoczył. Jeden ruch to jeden zakres.</summary>
    /// <remarks>Dotyczy wyłącznie drogi bez sąsiadów, gdzie rozstrzyga się w trakcie ruchu.</remarks>
    private bool _gestZuzyty;

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
    private void ObszarGest(object? nadawca, ScrollGestureEventArgs e)
    {
        _gest += e.Delta;

        var wBok = -_gest.X;
        var wPion = -_gest.Y;

        // W bok wyraźnie bardziej niż w pionie — inaczej dojeżdżanie do wieczora
        // ciągnęłoby siatkę w bok przy każdym ukośnym ruchu.
        if (Math.Abs(wBok) < ProgSledzenia || Math.Abs(wBok) < 1.5 * Math.Abs(wPion))
        {
            return;
        }

        // **Za palcem tylko wtedy, gdy jest co odsłonić.** Bez złożonych sąsiadów
        // przeciąganie pokazuje puste tło, a to jest gorsze od braku ruchu: udaje
        // płynność i pokazuje dziurę tam, gdzie powinien być następny tydzień.
        // Wtedy siatka po prostu przeskakuje — czyli robi to, co robiła zawsze.
        if (_kalendarz?.SasiedziGotowi != true)
        {
            if (!_gestZuzyty)
            {
                _gestZuzyty = Przeskocz(wBok, wPion);
            }

            return;
        }

        Czuwaj();
        Przesun(wBok);
    }

    private void ObszarGestSkonczony(object? nadawca, ScrollGestureEndedEventArgs e) =>
        ZakonczGest();

    /// <summary>Ustawienie siatki na zadanym przesunięciu — bez animacji, wprost za palcem.</summary>
    private void Przesun(double wBok)
    {
        if (_obszarKalendarza is not { Bounds.Width: > 0 } obszar)
        {
            return;
        }

        OdslonSasiadow(obszar.Bounds.Width);

        _przesuniecieSiatki ??= new TranslateTransform();
        obszar.RenderTransform = _przesuniecieSiatki;

        // Ograniczone do szerokości: dalej i tak nie ma czego odsłaniać, a siatka
        // wyjechana poza ekran wygląda na zgubioną.
        _przesuniecieSiatki.X = Math.Clamp(wBok, -obszar.Bounds.Width, obszar.Bounds.Width);
    }

    /// <summary>
    /// Pokazanie sąsiednich zakresów po bokach siatki.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stoją w tym samym panelu, co siatka, odsunięte o szerokość okna w lewo i w prawo.
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
    private void OdslonSasiadow(double szerokosc)
    {
        // Warstwy odszukiwane przy pierwszym użyciu, nie przy wczytaniu panelu, i to
        // jest zabezpieczenie przed jedyną przyczyną, której poprzednim razem nie
        // wykluczyłem: gdyby odszukanie po nazwie zawiodło, pola zostałyby puste,
        // podglądy nigdy nie zapaliłyby się i wyglądałoby to dokładnie tak samo jak
        // brak danych. Zapis w dzienniku rozdziela te dwie przyczyny.
        _podgladPrzed ??= this.FindControl<Panel>("PodgladPrzed");
        _podgladPo ??= this.FindControl<Panel>("PodgladPo");

        if ((_podgladPrzed is null || _podgladPo is null) && !_brakWarstwZapisany)
        {
            _brakWarstwZapisany = true;

            _ = Probuj("Kalendarz: podgląd sąsiadów", () =>
                DataContext is MainViewModel model
                    ? model.Journal.RecordAsync(
                        "Kalendarz: podgląd sąsiadów",
                        "nie ma warstw",
                        "Warstwy PodgladPrzed/PodgladPo nie zostały odnalezione w oknie.")
                    : Task.CompletedTask);
        }

        Ustaw(_podgladPrzed, -szerokosc);
        Ustaw(_podgladPo, szerokosc);

        static void Ustaw(Panel? podglad, double gdzie)
        {
            if (podglad is null)
            {
                return;
            }

            if (podglad.RenderTransform is not TranslateTransform przesuniecie)
            {
                przesuniecie = new TranslateTransform();
                podglad.RenderTransform = przesuniecie;
            }

            przesuniecie.X = gdzie;
            podglad.IsVisible = true;
        }
    }

    /// <summary>Schowanie sąsiadów. Poza gestem nie mają czego pokazywać.</summary>
    private void SchowajSasiadow()
    {
        if (_podgladPrzed is not null)
        {
            _podgladPrzed.IsVisible = false;
        }

        if (_podgladPo is not null)
        {
            _podgladPo.IsVisible = false;
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
    private void ZakonczGest()
    {
        var wBok = -_gest.X;
        var wPion = -_gest.Y;

        _gest = default;
        _czuwanie?.Stop();

        if (_gestZuzyty)
        {
            _gestZuzyty = false;
            return;
        }

        var skad = _przesuniecieSiatki?.X ?? 0;

        if (Math.Abs(skad) < 0.5)
        {
            // Siatka nigdy nie ruszyła — to nie było przejechanie, tylko przewijanie
            // albo dotknięcie. Nie ma czego kończyć.
            return;
        }

        Przeskocz(wBok, wPion);
        Wroc(skad);
    }

    /// <summary>Sąsiedzi chowani po dojeździe, nie przed nim — inaczej znikliby w ruchu.</summary>
    private async Task PoDojezdzieAsync(Task dojazd)
    {
        await dojazd;
        SchowajSasiadow();
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
    private DispatcherTimer? _czuwanie;

    private void Czuwaj()
    {
        _czuwanie ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Input,
            (_, _) => ZakonczGest());

        _czuwanie.Stop();
        _czuwanie.Start();
    }

    private (Point Skad, Point Dokad, IPointer Wskaznik)? _przejechanie;

    private void ObszarNacisniety(object? nadawca, PointerPressedEventArgs e)
    {
        // Gest palca ma własną drogę; tu zostaje mysz, bo jej nikt nie przejmuje.
        if (nadawca is not Control obszar || e.Pointer.Type != PointerType.Mouse)
        {
            _przejechanie = null;
            return;
        }

        var punkt = e.GetPosition(obszar);
        _przejechanie = (punkt, punkt, e.Pointer);
    }

    private void ObszarRuch(object? nadawca, PointerEventArgs e)
    {
        if (_przejechanie is not { } dotyk
            || nadawca is not Control obszar
            || !ReferenceEquals(dotyk.Wskaznik, e.Pointer))
        {
            return;
        }

        var dokad = e.GetPosition(obszar);
        _przejechanie = dotyk with { Dokad = dokad };

        var wBok = dokad.X - dotyk.Skad.X;
        var wPion = dokad.Y - dotyk.Skad.Y;

        // Mysz idzie za ręką tak samo jak palec — ten sam próg i ten sam warunek.
        if (!_przeciagam
            && Math.Abs(wBok) >= ProgSledzenia
            && Math.Abs(wBok) >= 1.5 * Math.Abs(wPion))
        {
            Przesun(wBok);
        }
    }

    private void ObszarPuszczony(object? nadawca, PointerReleasedEventArgs e)
    {
        var ruch = _przejechanie;
        _przejechanie = null;

        if (ruch is not { } dotyk || !ReferenceEquals(dotyk.Wskaznik, e.Pointer))
        {
            // Palec: zapasowe zakończenie gestu na wypadek, gdyby biblioteka nie
            // ogłosiła jego końca. Gdy siatka nigdy nie ruszyła, nic się nie dzieje.
            ZakonczGest();
            return;
        }

        var dokad = nadawca is Control obszar ? e.GetPosition(obszar) : dotyk.Dokad;

        var wBok = dokad.X - dotyk.Skad.X;
        var wPion = dokad.Y - dotyk.Skad.Y;

        var skad = _przesuniecieSiatki?.X ?? 0;

        // Obsłużone tylko wtedy, gdy naprawdę przejechano — inaczej zwykłe kliknięcie
        // bloku przestałoby go otwierać.
        e.Handled = Przeskocz(wBok, wPion);

        if (Math.Abs(skad) >= 0.5)
        {
            Wroc(skad);
        }
    }

    /// <summary>Czy ruch o tyle punktów jest przejechaniem — i jeśli tak, przeskakuje.</summary>
    private bool Przeskocz(double wBok, double wPion)
    {
        // Przeciąganie bloku też jedzie w bok — i to ono ma wtedy znaczenie, nie zakres.
        if (_przeciagam || _kalendarz is null)
        {
            return false;
        }

        // W bok **wyraźnie bardziej** niż w pionie: ukośny ruch przy przewijaniu dnia
        // przeskakiwałby tydzień przy każdej próbie dojechania do wieczora.
        if (Math.Abs(wBok) < ProgPrzejechania || Math.Abs(wBok) < 1.5 * Math.Abs(wPion))
        {
            return false;
        }

        // Palec w lewo odsłania to, co po prawej — czyli następny zakres. Tak samo
        // zachowuje się każda lista, po której się przejeżdża.
        _ = Probuj(
            "Kalendarz: przejechanie",
            () => wBok < 0
                ? _kalendarz.NextCommand.ExecuteAsync(null)
                : _kalendarz.PreviousCommand.ExecuteAsync(null));

        return true;
    }

    /// <summary>Jak długo siatka dojeżdża na miejsce po puszczeniu.</summary>
    /// <remarks>
    /// Sto sześćdziesiąt milisekund: dość, żeby ruch był ruchem, a nie podmianą,
    /// i za mało, żeby zdążyło się na niego czekać.
    /// </remarks>
    private static readonly TimeSpan CzasDojazdu = TimeSpan.FromMilliseconds(160);

    /// <summary>Przesunięcie rysowania siatki. Jedno na całe życie okna.</summary>
    /// <remarks>
    /// Przesuwane jest rysowanie, nie układ: siatka zostaje tam, gdzie była, więc nic
    /// się nie przelicza i nic nie zmienia rozmiaru.
    /// </remarks>
    private TranslateTransform? _przesuniecieSiatki;

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
    private void Wroc(double skad) =>
        _ = Probuj("Kalendarz: dojazd siatki", () => PoDojezdzieAsync(WrocAsync(skad)));

    private Task WrocAsync(double skad)
    {
        if (_przesuniecieSiatki is not { } przesuniecie)
        {
            return Task.CompletedTask;
        }

        var animacja = new Animation
        {
            Duration = CzasDojazdu,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(TranslateTransform.XProperty, skad) },
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
        przesuniecie.X = 0;

        return animacja.RunAsync(przesuniecie);
    }

    /// <summary>Warstwa linii godzin — pionowy punkt odniesienia dla przeciągania.</summary>
    /// <remarks>
    /// Wszystkie kolumny mają tę samą górną krawędź siatki, więc wystarczy jedna:
    /// wysokość liczona względem niej jest prawdziwa niezależnie od tego, nad którym
    /// dniem stoi wskaźnik.
    /// </remarks>
    private Control? _warstwaGodzin;

    private void WarstwaGodzinGotowa(object? nadawca, RoutedEventArgs e) =>
        _warstwaGodzin = nadawca as Control;

    /// <summary>Ile trzeba przejechać, żeby to było przeciąganie, a nie drgnięcie ręki.</summary>
    private const double ProgPrzeciagniecia = 6;

    private SlotBox? _wciesniety;

    private Point _skad;

    private bool _przeciagam;

    /// <summary>Czy trwające przeciąganie zmienia długość, a nie porę.</summary>
    private bool _rozciagam;

    /// <summary>Kursory tworzone raz. Nowy przy każdym drgnięciu myszy to nowy zasób systemu.</summary>
    private static readonly Cursor KursorReki = new(StandardCursorType.Hand);

    private static readonly Cursor KursorKrawedzi = new(StandardCursorType.SizeNorthSouth);

    /// <summary>Ile punktów od dolnej krawędzi bloku łapie za jego koniec.</summary>
    /// <remarks>
    /// Sześć, tyle samo co próg przeciągnięcia. Więcej znaczyłoby, że kwadransowy blok
    /// jest w połowie krawędzią i nie da się go już przesunąć; mniej, że w krawędź
    /// trzeba celować.
    /// </remarks>
    private const double StrefaKrawedzi = 6;

    /// <summary>
    /// Ile punktów ma godzina na siatce.
    /// </summary>
    /// <remarks>
    /// Ta sama liczba co w modelu kalendarza, powtórzona tutaj świadomie: podgląd
    /// rysuje okno, a nie model, i gdyby sięgał po nią przez model, okno zależałoby
    /// od jego wnętrza po to, żeby narysować prostokąt.
    /// </remarks>
    private const double WysokoscGodziny = 48;

    /// <summary>Punkt wewnątrz bloku, za który go złapano.</summary>
    private Point _chwyt;

    /// <summary>Lewy górny róg chwyconego bloku we współrzędnych okna.</summary>
    private Point? _blokNaEkranie;

    private void BlokWcisniety(object? nadawca, PointerPressedEventArgs e)
    {
        if (nadawca is not Control blok || blok.Tag is not SlotBox slot)
        {
            return;
        }

        // Tylko lewym przyciskiem. Prawy otwiera menu podręczne, a zapamiętany przy nim
        // blok zostawał w ręku: po usunięciu zadania z menu puszczenie nie miało już
        // dokąd trafić, więc wskaźnik do końca sesji zachowywał się tak, jakby wciąż
        // coś ciągnął — i nie dało się z tym nic zrobić.
        if (!e.GetCurrentPoint(blok).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _wciesniety = slot;
        _skad = e.GetPosition(this);
        _przeciagam = false;

        // Myszą przeciąga się od razu; palcem dopiero po przytrzymaniu. Na dotyku
        // ruch palca po bloku znaczy najczęściej „przewiń widok", a nie „przenieś to
        // zadanie" — a blok, który przejmuje wskaźnik po sześciu punktach, odbiera
        // przewijanie wszędzie tam, gdzie coś stoi. Czyli w zajęty dzień prawie wszędzie.
        _wolnoPrzeciagac = e.Pointer.Type != PointerType.Touch;

        if (!_wolnoPrzeciagac)
        {
            _przytrzymanie?.Stop();
            _przytrzymanie = new DispatcherTimer(
                Przytrzymanie, DispatcherPriority.Input, (zegar, _) =>
                {
                    (zegar as DispatcherTimer)?.Stop();
                    _wolnoPrzeciagac = true;
                });

            _przytrzymanie.Start();
        }

        // Gdzie **w bloku** wylądowała ręka i gdzie ten blok stoi na ekranie. Jedno
        // i drugie po to, żeby kopia w podglądzie trzymała się tego samego punktu,
        // za który została złapana, zamiast skakać rogiem pod wskaźnik.
        _chwyt = e.GetPosition(blok);
        _blokNaEkranie = blok.TranslatePoint(new Point(0, 0), this);

        // Za dolną krawędź ciągnie się koniec, nie cały blok. Tylko przy zadaniach:
        // długość wydarzenia z cudzego kalendarza zmienia się świadomą drogą.
        _rozciagam = slot.TaskId is not null
            && e.GetPosition(blok).Y >= blok.Bounds.Height - StrefaKrawedzi;
    }

    /// <summary>
    /// Kształt wskaźnika nad blokiem: strzałka albo znak rozciągania przy krawędzi.
    /// </summary>
    /// <remarks>
    /// Bez tego chwyt za koniec bloku jest wiedzą tajemną — nic na ekranie nie mówi,
    /// że dolne sześć punktów robi co innego niż reszta.
    /// </remarks>
    private void BlokNajechany(object? nadawca, PointerEventArgs e)
    {
        if (nadawca is not Control blok || blok.Tag is not SlotBox slot)
        {
            return;
        }

        var przyKrawedzi = slot.TaskId is not null
            && e.GetPosition(blok).Y >= blok.Bounds.Height - StrefaKrawedzi;

        blok.Cursor = przyKrawedzi ? KursorKrawedzi : KursorReki;
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
    private static readonly TimeSpan Przytrzymanie = TimeSpan.FromMilliseconds(400);

    /// <summary>Minutnik przytrzymania i jego wynik. Mysz ma zgodę od razu.</summary>
    private DispatcherTimer? _przytrzymanie;

    private bool _wolnoPrzeciagac = true;

    private void PuscBlok()
    {
        _przytrzymanie?.Stop();
        _wolnoPrzeciagac = true;
        _wciesniety = null;
        _przeciagam = false;
        _rozciagam = false;
        SchowajPodglad();
    }

    private void BlokPuscilWskaznik(object? nadawca, PointerCaptureLostEventArgs e)
    {
        if (nadawca is Control blok)
        {
            blok.Opacity = 1;
        }

        PuscBlok();
    }

    /// <summary>Dzień i wysokość pod wskaźnikiem, przeliczone na współrzędne siatki.</summary>
    /// <remarks>
    /// Z położenia wskaźnika, a nie z tego, co pod nim: przy przechwyconym wskaźniku
    /// zdarzenia trafiają do przeciąganego bloku niezależnie od tego, nad czym stoi.
    /// </remarks>
    private (DateOnly Dzien, double Wysokosc)? Cel(PointerEventArgs e)
    {
        if (_kalendarz is null
            || _warstwaGodzin is null
            || this.FindControl<ItemsControl>("KolumnyDni") is not { } kolumny)
        {
            return null;
        }

        var szerokosc = _kalendarz.ColumnWidth + 2;
        var numer = Math.Clamp(
            (int)(e.GetPosition(kolumny).X / szerokosc), 0, _kalendarz.VisibleDays - 1);

        return (_kalendarz.Anchor.AddDays(numer), e.GetPosition(_warstwaGodzin).Y);
    }

    /// <summary>
    /// Podgląd przeciąganego bloku: co i dokąd.
    /// </summary>
    /// <remarks>
    /// Bez niego przeciąganie jest ruchem w ciemno — o właściwej godzinie dowiadujesz
    /// się dopiero po puszczeniu, czyli po zapisie. Przy wydarzeniach z Google znaczy
    /// to po zapisie w cudzym kalendarzu.
    /// </remarks>
    private void PokazPodglad(PointerEventArgs e, SlotBox slot)
    {
        if (this.FindControl<Border>("Podglad") is not { } podglad
            || this.FindControl<TextBlock>("PodgladTytul") is not { } tytul
            || this.FindControl<TextBlock>("PodgladOd") is not { } od
            || this.FindControl<TextBlock>("PodgladDo") is not { } doGodz
            || this.FindControl<StackPanel>("PodgladGodziny") is not { } godziny
            || Cel(e) is not var (dzien, wysokosc))
        {
            return;
        }

        var poczatek = CalendarViewModel.Pora(slot.Top);
        var pora = CalendarViewModel.Pora(wysokosc);

        podglad.Width = slot.Width;
        podglad.Background = slot.Background;
        tytul.Text = slot.Title;

        if (_rozciagam)
        {
            // Rozciąganie: blok stoi tam, gdzie stał, i rośnie w dół za wskaźnikiem.
            // Dzień się nie zmienia, więc pokazanie go sugerowałoby, że gdzieś jedzie.
            var koniec = TimeOnly.FromTimeSpan(
                pora.ToTimeSpan() > poczatek.ToTimeSpan()
                    ? pora.ToTimeSpan()
                    : poczatek.ToTimeSpan() + TimeSpan.FromMinutes(5));

            podglad.Height = Math.Max(
                12, (koniec.ToTimeSpan() - poczatek.ToTimeSpan()).TotalHours * WysokoscGodziny);

            od.Text = $"{poczatek:HH}:{poczatek:mm}";
            doGodz.Text = $"{koniec:HH}:{koniec:mm}";
            godziny.IsVisible = true;

            if (_blokNaEkranie is { } rog)
            {
                Canvas.SetLeft(podglad, rog.X);
                Canvas.SetTop(podglad, rog.Y);
            }
        }
        else
        {
            // Koniec liczony z długości bloku, nie z jego starej godziny: przeciągnięcie
            // przesuwa, a nie skraca. Doba przycięta, żeby blok zaczepiony pod wieczór
            // nie pokazywał godziny z następnego dnia.
            var suma = pora.ToTimeSpan() + TimeSpan.FromHours(slot.Height / WysokoscGodziny);
            var koniec = TimeOnly.FromTimeSpan(
                suma < TimeSpan.FromDays(1) ? suma : TimeSpan.FromDays(1) - TimeSpan.FromMinutes(5));

            podglad.Height = slot.Height;
            od.Text = $"{dzien:dd.MM} {pora:HH}:{pora:mm}";
            doGodz.Text = $"{koniec:HH}:{koniec:mm}";
            godziny.IsVisible = slot.Width >= 120;

            var gdzie = e.GetPosition(this);
            Canvas.SetLeft(podglad, gdzie.X - _chwyt.X);
            Canvas.SetTop(podglad, gdzie.Y - _chwyt.Y);
        }

        podglad.IsVisible = true;
    }

    private void SchowajPodglad()
    {
        _blokNaEkranie = null;

        if (this.FindControl<Border>("Podglad") is { } podglad)
        {
            podglad.IsVisible = false;
        }
    }

    private void BlokRuszony(object? nadawca, PointerEventArgs e)
    {
        // Kształt wskaźnika liczony przy każdym ruchu, także bez wciśnięcia: to jedyne
        // miejsce, po którym widać, że dolna krawędź robi co innego niż reszta bloku.
        BlokNajechany(nadawca, e);

        if (_wciesniety is not { } slot)
        {
            return;
        }

        // Zadania ruszamy zawsze, wydarzenia tylko takie, które mają dokąd wrócić.
        var doRuszenia = slot.TaskId is not null
            || (slot.SourceId is not null && slot.ExternalId is not null);

        if (!doRuszenia)
        {
            return;
        }

        if (!_przeciagam)
        {
            var teraz = e.GetPosition(this);

            if (Math.Abs(teraz.X - _skad.X) <= ProgPrzeciagniecia
                && Math.Abs(teraz.Y - _skad.Y) <= ProgPrzeciagniecia)
            {
                return;
            }

            // Palec ruszył, zanim minęło przytrzymanie: ten gest należy do przewijania.
            // Blok wypuszczamy z ręki na dobre, żeby przytrzymanie, które minie
            // w połowie przewijania, nie porwało go w locie.
            if (!_wolnoPrzeciagac)
            {
                PuscBlok();
                return;
            }

            _przeciagam = true;

            if (nadawca is Control blok)
            {
                blok.Opacity = 0.5;
                e.Pointer.Capture(blok);
            }
        }

        PokazPodglad(e, slot);
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
    private void BlokPuszczony(object? nadawca, PointerReleasedEventArgs e)
    {
        // Stan odczytany **przed** oddaniem wskaźnika. Oddanie zgłasza utratę
        // przechwycenia, a ta kończy chwyt i zeruje te trzy pola — czytane po niej
        // dałyby każde puszczenie jako kliknięcie i przeciąganie przestałoby działać.
        var slot = _wciesniety;
        var przeciagniete = _przeciagam;
        var rozciagane = _rozciagam;

        PuscBlok();

        if (nadawca is Control blok)
        {
            blok.Opacity = 1;
            e.Pointer.Capture(null);
        }

        // Puszczenie bez przejechania progu jest kliknięciem. Przycisk robił to za nas,
        // ale przy okazji zjadał wciśnięcie i przeciąganie nie miało jak się zacząć.
        if (!przeciagniete)
        {
            if (slot is not null)
            {
                _kalendarz?.OpenTaskCommand.Execute(slot);
            }

            return;
        }

        if (_kalendarz is null || slot is null)
        {
            return;
        }

        if (Cel(e) is not var (dzien, wysokosc) || _kalendarz is null)
        {
            return;
        }

        if (rozciagane)
        {
            _ = Probuj("Kalendarz: rozciągnięcie", () => _kalendarz.ResizeAsync(slot, wysokosc));
            return;
        }

        // Zadanie idzie naszą drogą, wydarzenie — prosto do kalendarza, z którego
        // pochodzi. To druga rzecz, nie ta sama z innym zapisem.
        _ = slot.TaskId is { } zadanie
            ? Probuj("Kalendarz: przełożenie", () => _kalendarz.MoveAsync(zadanie, dzien, wysokosc))
            : Probuj(
                "Kalendarz: przeniesienie wydarzenia",
                () => _kalendarz.MoveEventAsync(slot, dzien, wysokosc));
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
    private (SlotBox Blok, Point Skad, IPointer Wskaznik)? _dotkniety;

    /// <remarks>
    /// Tak samo jak przy pustej siatce: odhacza puszczenie, a nie naciśnięcie.
    /// Kwadracik jest mały, ale leży na bloku zadania, a przewijanie zaczyna się tam,
    /// gdzie akurat wylądował palec — odhaczenie zadania przy próbie przesunięcia
    /// widoku jest gorsze od nieodhaczenia go wcale.
    /// </remarks>
    private void OdhaczNaSiatce(object? nadawca, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _dotkniety = null;

        if (nadawca is not Control kwadracik || kwadracik.Tag is not SlotBox blok)
        {
            return;
        }

        _dotkniety = (blok, e.GetPosition(kwadracik), e.Pointer);
    }

    private void OdhaczeniePuszczone(object? nadawca, PointerReleasedEventArgs e)
    {
        var start = _dotkniety;
        _dotkniety = null;

        if (start is not { } dotyk
            || nadawca is not Control kwadracik
            || _kalendarz is null
            || !ReferenceEquals(dotyk.Wskaznik, e.Pointer))
        {
            return;
        }

        var koniec = e.GetPosition(kwadracik);

        if (Math.Abs(koniec.X - dotyk.Skad.X) > ProgPrzeciagniecia
            || Math.Abs(koniec.Y - dotyk.Skad.Y) > ProgPrzeciagniecia)
        {
            return;
        }

        // Jedno polecenie na oba rodzaje bloku i oba kierunki. Okno nie musi wiedzieć,
        // czy pod spodem idzie zapis do bazy, czy zmiana nazwy w cudzym kalendarzu.
        _ = Probuj("Kalendarz: kwadracik", () => _kalendarz.ToggleCommand.ExecuteAsync(dotyk.Blok));
    }

    private void OdhaczeniePorzucone(object? nadawca, PointerCaptureLostEventArgs e) =>
        _dotkniety = null;

    /// <summary>Kliknięcie w przyciemnione tło zamyka okno szczegółu.</summary>
    private void TloSzczegolu(object? nadawca, PointerPressedEventArgs e) => _szczegol?.Close();

    /// <summary>Kliknięcie obok karty wydarzenia zamyka ją.</summary>
    /// <remarks>
    /// To samo, co przy karcie zadania, i z tego samego powodu: karta zasłania kalendarz,
    /// a najbliższą rzeczą, w którą trafia ręka chcąca wrócić do siatki, jest siatka.
    /// </remarks>
    private void TloWydarzenia(object? nadawca, PointerPressedEventArgs e) =>
        _kalendarz?.CloseOpenedCommand.Execute(null);

    /// <summary>Zatrzymanie kliknięcia na ramce okna, żeby nie doszło do tła.</summary>
    private void ZatrzymajKlikniecie(object? nadawca, PointerPressedEventArgs e) =>
        e.Handled = true;

    /// <summary>
    /// Escape zamyka to, co jest otwarte na wierzchu.
    /// </summary>
    /// <remarks>
    /// Kolejność od najbardziej wierzchniego: szczegół zadania, potem karta wydarzenia,
    /// potem lista „Więcej". Zamykanie wszystkiego naraz zabierałoby okno, którego
    /// nikt nie chciał zamykać.
    /// </remarks>
    private void NaKlawiszu(object? nadawca, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = Cofnij();
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
    private bool Cofnij()
    {
        if (DataContext is not MainViewModel model)
        {
            return false;
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
            _ = Probuj("Skrzynka: wyjście z przetwarzania",
                () => model.ShowInboxCommand.ExecuteAsync(null));
        }
        else
        {
            return false;
        }

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
    private void NaMenu(object? nadawca, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainViewModel model || e.Source is not Control zrodlo)
        {
            return;
        }

        // Cokolwiek trzymaliśmy w ręku, menu to kończy. Pozycja „Usuń" wyjmuje blok
        // z siatki, więc puszczenie nie miałoby już dokąd trafić.
        PuscBlok();

        if (Zadanie(zrodlo) is { } zadanie)
        {
            e.Handled = true;
            _ = Probuj("Menu: otwarcie", () => PokazMenuAsync(model, zrodlo, zadanie));

            return;
        }

        if (Wiersz(zrodlo) is { } wiersz)
        {
            e.Handled = true;
            _ = Probuj("Menu: projekt", () => PokazMenuProjektuAsync(model, zrodlo, wiersz));

            return;
        }

        if (Blok(zrodlo) is not { } blok)
        {
            return;
        }

        // Blok na siatce niesie sam identyfikator, nie całe zadanie — trzeba je dobrać.
        if (blok.TaskId is { } identyfikator)
        {
            e.Handled = true;

            _ = Probuj("Menu: otwarcie", async () =>
            {
                if (await model.FindTaskAsync(identyfikator) is { } zBazy)
                {
                    await PokazMenuAsync(model, zrodlo, zBazy);
                }
            });

            return;
        }

        e.Handled = true;
        PokazMenuWydarzenia(model, zrodlo, blok);
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
    private static void PokazMenuWydarzenia(MainViewModel model, Control zrodlo, SlotBox blok)
    {
        var kalendarz = model.Calendar;
        var pozycje = new List<MenuItem>();

        var otworz = new MenuItem { Header = "Otwórz" };
        otworz.Click += (_, _) => kalendarz.OpenTaskCommand.Execute(blok);
        pozycje.Add(otworz);

        if (blok.CanComplete)
        {
            var odhacz = new MenuItem { Header = blok.IsDone ? "Zdejmij ptaszek" : "Odhacz" };
            odhacz.Click += (_, _) => kalendarz.ToggleCommand.Execute(blok);
            pozycje.Add(odhacz);
        }

        new ContextMenu { ItemsSource = pozycje }.Open(zrodlo);
    }

    /// <summary>Blok siatki spod wskaźnika.</summary>
    private static SlotBox? Blok(Control zrodlo) =>
        zrodlo.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(k => k.DataContext)
            .OfType<SlotBox>()
            .FirstOrDefault();

    private async Task PokazMenuAsync(MainViewModel model, Control zrodlo, TaskItem zadanie)
    {
        var dzis = model.Dzisiaj;
        var projekty = await model.ActiveProjectsAsync();
        var kalendarze = await model.WritableCalendarsAsync();

        // Wyrażenie kolekcji, nie `new object[] { … }`: rozwinięcie `..` jest częścią
        // tego pierwszego, a w inicjalizatorze tablicy dwie kropki znaczą zakres.
        object[] pozycje =
        [
            Pozycja("Otwórz szczegół", () => model.OpenTaskAsync(zadanie)),

            // Jedna pozycja, dwa kierunki — zależnie od tego, jak zadanie stoi.
            // Obie naraz kazałyby czytać, która jest teraz właściwa.
            zadanie.State == TaskState.Done
                ? Pozycja("Zdejmij ptaszek", () => model.ReopenTaskAsync(zadanie))
                : Pozycja("Odhacz", () => model.CompleteTaskAsync(zadanie)),

            new Separator(),

            Galaz("Ustaw dzień", [
                Pozycja("Dziś", () => model.SetDateAsync(zadanie, dzis)),
                Pozycja("Jutro", () => model.SetDateAsync(zadanie, dzis.AddDays(1))),
                Pozycja("Za tydzień", () => model.SetDateAsync(zadanie, dzis.AddDays(7))),
                Pozycja("Bez dnia", () => model.SetDateAsync(zadanie, null)),
            ]),

            Galaz("Waga", [.. PriorityChoice.All.Select(w =>
                Pozycja(w.Label, () => model.SetPriorityAsync(zadanie, w.Value)))]),

            // Oszacowanie i siła są tu, bo bez nich zadanie nigdy nie wypłynie
            // w „Teraz”: ten ekran pyta „ile mam czasu i sił”, a zadanie, które
            // na to nie odpowiada, nie ma jak zostać wybrane. Do dziś dawało się
            // je wpisać tylko przy przetwarzaniu skrzynki albo w szczegółach.
            Galaz("Ile zajmie", [.. EstimateChoice.All.Select(m =>
                Pozycja(
                    // „Bez znaczenia" jest odpowiedzią filtra, nie zadania: tu ta
                    // sama wartość znaczy, że oszacowania **nie ma**.
                    m.Value is null ? "bez oszacowania" : m.Label,
                    () => model.SetEstimateAsync(zadanie, m.Value)))]),

            Galaz("Ile sił", [.. EnergyChoice.All.Select(e =>
                Pozycja(e.Label, () => model.SetEnergyAsync(zadanie, e.Value)))]),

            Galaz("Rytm", [.. RepeatChoice.All.Select(r =>
                Pozycja(r.Label, () => model.SetRecurrenceAsync(zadanie, r.Kind)))]),

            Galaz("Projekt", [
                Pozycja("Bez projektu", () => model.SetProjectAsync(zadanie, null)),
                .. projekty.Select(p =>
                    Pozycja(p.Outcome, () => model.SetProjectAsync(zadanie, p.Id))),
            ]),

            new Separator(),

            // Udostępnianie pojedynczego zadania, nie całego obszaru: obszar
            // rodzinny mieści i „odebrać dziecko”, i „kupić prezent”, a widzieć
            // je mają różne osoby. Gałąź pokazuje się tylko wtedy, gdy jest dokąd
            // udostępniać — pozycja bez skutku uczy nieufności do całego menu.
            .. Udostepnianie(model, zadanie, kalendarze),

            Pozycja("Weź na dziś", () => model.FocusTaskAsync(zadanie)),
            Pozycja("Pokaż w kalendarzu", () => model.ShowInCalendarAsync(zadanie)),
            Pozycja("Zamień na notatkę", () => model.ToNoteAsync(zadanie)),
            new Separator(),
            Pozycja("Usuń", () => model.TrashTaskAsync(zadanie)),
        ];

        new MenuFlyout { ItemsSource = pozycje }.ShowAt(zrodlo, showAtPointer: true);
    }

    /// <summary>Wiersz ekranu „Projekty” spod wskaźnika.</summary>
    private static ProjectTreeRow? Wiersz(Control zrodlo) =>
        zrodlo.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(k => k.DataContext)
            .OfType<ProjectTreeRow>()
            .FirstOrDefault();

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
    private void NaWpisieMiesiaca(object? nadawca, PointerReleasedEventArgs e)
    {
        if (DataContext is not MainViewModel model
            || nadawca is not Control wiersz
            || wiersz.DataContext is not MonthEntry wpis)
        {
            return;
        }

        e.Handled = true;
        model.Calendar.OpenMonthEntry(wpis);
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
    private void NaDniuMiesiaca(object? nadawca, PointerReleasedEventArgs e)
    {
        if (DataContext is not MainViewModel model
            || nadawca is not Control komorka
            || komorka.DataContext is not MonthCell dzien)
        {
            return;
        }

        e.Handled = true;
        _ = Probuj("Kalendarz: dzień z miesiąca", () => model.Calendar.OpenMonthDayCommand.ExecuteAsync(dzien));
    }

    private void NaBarwie(object? nadawca, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel model
            || nadawca is not Control kwadracik
            || kwadracik.DataContext is not ProjectTreeRow wiersz)
        {
            return;
        }

        e.Handled = true;
        PokazPalete(model, kwadracik, wiersz);
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
    private void PokazPalete(MainViewModel model, Control zrodlo, ProjectTreeRow wiersz)
    {
        var kolo = new ColorView
        {
            Color = Color.TryParse(wiersz.Color ?? string.Empty, out var biezaca)
                ? biezaca
                : Colors.SlateGray,
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            Width = 300,
        };

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        var ustaw = new Button { Content = "Ustaw", Padding = new Thickness(14, 6) };
        var wyczysc = new Button { Content = "Bez barwy", Padding = new Thickness(14, 6) };

        ustaw.Click += (_, _) =>
        {
            flyout.Hide();
            var c = kolo.Color;
            _ = Probuj(
                "Barwa: ustawienie",
                () => model.SetRowColorAsync(wiersz, $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
        };

        wyczysc.Click += (_, _) =>
        {
            flyout.Hide();
            _ = Probuj("Barwa: zdjęcie", () => model.SetRowColorAsync(wiersz, null));
        };

        // Barwy przygotowane zostają nad kołem, jednym kliknięciem. Koło jest po to,
        // żeby dało się wyjść poza listę, a nie po to, żeby za każdym razem trzeba
        // było trafiać myszą w odcień, który i tak jest na liście.
        var szybkie = new WrapPanel();

        foreach (var barwa in ColorChoice.All.Where(b => b.Value is not null))
        {
            var wybor = barwa;
            // Barwa na ramce w środku, nie na tle przycisku: tło przycisku motyw
            // przemalowuje przy najechaniu, a kwadracik, który zmienia kolor pod
            // wskaźnikiem, przestaje pokazywać to, co ma pokazywać.
            var kwadracik = new Button
            {
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(7),
                [ToolTip.TipProperty] = wybor.Label,
                Content = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.Parse(wybor.Value!)),
                },
            };

            kwadracik.Click += (_, _) =>
            {
                flyout.Hide();
                _ = Probuj("Barwa: ustawienie", () => model.SetRowColorAsync(wiersz, wybor.Value));
            };

            szybkie.Children.Add(kwadracik);
        }

        flyout.Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                szybkie,
                kolo,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Children = { ustaw, wyczysc },
                },
            },
        };

        flyout.ShowAt(zrodlo);
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
    private async Task PokazMenuProjektuAsync(MainViewModel model, Control zrodlo, ProjectTreeRow wiersz)
    {
        var przeszkoda = await model.WhyCannotDeleteRowAsync(wiersz);

        var pozycje = new List<object>
        {
            Pozycja("Zmień nazwę…", () =>
            {
                PokazPoleNazwy(
                    zrodlo, wiersz.Label, "Zapisz",
                    nazwa => model.RenameRowAsync(wiersz, nazwa));

                return Task.CompletedTask;
            }),
            Pozycja(wiersz.AddLabel, () =>
            {
                PokazPoleNazwy(
                    zrodlo, string.Empty, "Załóż",
                    nazwa => model.AddProjectAsync(wiersz, nazwa));

                return Task.CompletedTask;
            }),
            Pozycja("Barwa…", () =>
            {
                PokazPalete(model, zrodlo, wiersz);
                return Task.CompletedTask;
            }),
        };

        // Kalendarz tylko przy obszarze: projekt go nie ma i mieć nie powinien. Obszar
        // jest podziałem odpowiedzialności, a kalendarz Google jest tym samym podziałem
        // widzianym z zewnątrz — projekt jest o poziom za drobny, żeby zakładać dla
        // niego osobny kalendarz.
        if (wiersz.IsArea && await KalendarzObszaruAsync(model, wiersz) is { } kalendarze)
        {
            pozycje.Add(kalendarze);
        }

        pozycje.Add(new Separator());

        pozycje.Add(przeszkoda is null
            ? Pozycja(wiersz.IsArea ? "Usuń obszar" : "Usuń projekt",
                () => model.DeleteRowAsync(wiersz))
            : new MenuItem { Header = przeszkoda, IsEnabled = false });

        new MenuFlyout { ItemsSource = pozycje }.ShowAt(zrodlo, showAtPointer: true);
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
    private async Task<MenuItem?> KalendarzObszaruAsync(MainViewModel model, ProjectTreeRow wiersz)
    {
        var kalendarze = await model.WritableCalendarsAsync();

        if (kalendarze.Count == 0)
        {
            return null;
        }

        var teraz = await model.AreaCalendarAsync(wiersz.Id);

        var pozycje = new List<MenuItem>
        {
            Pozycja(teraz is null ? "✓ żaden" : "żaden",
                () => model.SetAreaCalendarAsync(wiersz, null)),
        };

        pozycje.AddRange(kalendarze.Select(k => Pozycja(
            teraz == k.Id ? $"✓ {k.Name}" : k.Name,
            () => model.SetAreaCalendarAsync(wiersz, k.Id))));

        return Galaz("Kalendarz Google", pozycje);
    }

    /// <summary>
    /// Małe okienko z jednym polem tekstowym przy wierszu.
    /// </summary>
    /// <remarks>
    /// Pole wprost na liście byłoby czwartą rzeczą w wierszu, który już niesie nazwę,
    /// kwadracik barwy i liczby — a nazwę zmienia się raz na parę miesięcy. Enter
    /// zapisuje, bo po wpisaniu ręka i tak tam idzie.
    /// </remarks>
    private void PokazPoleNazwy(
        Control zrodlo, string poczatkowa, string przycisk, Func<string, Task> praca)
    {
        var pole = new TextBox { Text = poczatkowa, Width = 260 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        void Zapisz()
        {
            var nazwa = pole.Text ?? string.Empty;
            flyout.Hide();
            _ = Probuj($"Struktura: {przycisk}", () => praca(nazwa));
        }

        pole.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                Zapisz();
            }
        };

        var zapisz = new Button { Content = przycisk, Padding = new Thickness(14, 6) };
        zapisz.Click += (_, _) => Zapisz();

        flyout.Content = new StackPanel { Spacing = 8, Children = { pole, zapisz } };
        flyout.ShowAt(zrodlo);

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
    private IReadOnlyList<object> Udostepnianie(
        MainViewModel model, TaskItem zadanie, IReadOnlyList<CalendarSource> kalendarze)
    {
        var gdzieJest = zadanie.SharedCalendarId;
        var glowny = model.MainCalendarId;

        var pozostale = kalendarze.Where(k => k.Id != gdzieJest).ToList();
        var pozycje = new List<object>();

        if (pozostale.Count > 0)
        {
            pozycje.Add(Galaz("Przenieś do kalendarza", [.. pozostale.Select(k =>
                Pozycja(k.Name, () => model.ShareTaskAsync(zadanie, k.Id)))]));
        }

        if (gdzieJest is not null && glowny is not null && gdzieJest != glowny)
        {
            pozycje.Add(Pozycja(
                "Z powrotem na kalendarz główny", () => model.UnshareTaskAsync(zadanie)));
        }

        return pozycje;
    }

    /// <summary>Pozycja menu. Woła metodę wprost — wyjątek ma dokąd trafić.</summary>
    private MenuItem Pozycja(string napis, Func<Task> praca)
    {
        var pozycja = new MenuItem { Header = napis };
        pozycja.Click += (_, _) => _ = Probuj($"Menu: {napis}", praca);

        return pozycja;
    }

    private static MenuItem Galaz(string napis, IReadOnlyList<MenuItem> pozycje) =>
        new() { Header = napis, ItemsSource = pozycje };

    /// <summary>Zadanie spod wskaźnika — niezależnie od tego, czym jest wiersz.</summary>
    private static TaskItem? Zadanie(Control zrodlo)
    {
        foreach (var przodek in zrodlo.GetSelfAndVisualAncestors().OfType<Control>())
        {
            var znalezione = przodek.DataContext switch
            {
                TaskItem wprost => wprost,
                TaskRow wiersz => wiersz.Task,
                WaitingItem czekajace => czekajace.Task,
                NowPick wybor => wybor.Task,
                _ => null,
            };

            if (znalezione is not null)
            {
                return znalezione;
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
    private void OtworzZListy(object? nadawca, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        // Z zaznaczenia listy, a gdy go nie ma — z wiersza pod wskaźnikiem. Ekran
        // „Teraz" nie jest listą do zaznaczania, tylko odpowiedzią na pytanie, więc
        // jego wiersze nie mają zaznaczenia w ogóle.
        var zadanie = (nadawca as ListBox)?.SelectedItem switch
        {
            TaskItem wprost => wprost,
            TaskRow wiersz => wiersz.Task,
            WaitingItem czekajace => czekajace.Task,
            NowPick wybor => wybor.Task,
            _ => e.Source is Control zrodlo ? Zadanie(zrodlo) : null,
        };

        if (zadanie is not null)
        {
            _ = Probuj("Lista: otwarcie zadania", () => model.Detail.LoadAsync(zadanie));
        }
    }

    /// <summary>Dwuklik na notatce otwiera ją do czytania i poprawiania.</summary>
    private void OtworzNotatke(object? nadawca, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model
            && nadawca is ListBox lista
            && lista.SelectedItem is Note notatka)
        {
            model.Notes.OpenCommand.Execute(notatka);
        }
    }

    /// <summary>Kliknięcie w pasek całodniowy — zadanie na cały dzień też ma szczegół.</summary>
    private void OtworzCalodniowe(object? nadawca, RoutedEventArgs e)
    {
        if (nadawca is Control przycisk && przycisk.Tag is AllDayBox wpis)
        {
            _kalendarz?.OpenAllDay(wpis);
        }
    }

    private void Zadanie(string co, Func<TaskDetailViewModel, Task> praca)
    {
        if (_szczegol is not { } model)
        {
            return;
        }

        _ = Probuj(co, () => praca(model));
    }

    private async Task Probuj(string co, Func<Task> praca)
    {
        try
        {
            await praca();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (DataContext is MainViewModel model)
            {
                await model.Journal.RecordAsync(
                    co, "nie udało się", $"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    private CalendarViewModel? _kalendarz;

    /// <summary>Gdzie i czym zaczęło się dotknięcie pustej siatki.</summary>
    private (DateOnly Dzien, Point Skad, IPointer Wskaznik)? _dotknieta;

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
    private void NowaRzeczNaSiatce(object? nadawca, PointerPressedEventArgs e)
    {
        _dotknieta = null;

        if (nadawca is not Control warstwa || warstwa.Tag is not DateOnly dzien)
        {
            return;
        }

        // Tylko lewy przycisk. Prawy i środkowy też dają PointerPressed, a zakładanie
        // zadania menu podręcznym byłoby niespodzianką.
        if (!e.GetCurrentPoint(warstwa).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dotknieta = (dzien, e.GetPosition(warstwa), e.Pointer);
    }

    private void SiatkaPuszczona(object? nadawca, PointerReleasedEventArgs e)
    {
        var start = _dotknieta;
        _dotknieta = null;

        if (start is not { } dotyk
            || nadawca is not Control warstwa
            || !ReferenceEquals(dotyk.Wskaznik, e.Pointer))
        {
            return;
        }

        var koniec = e.GetPosition(warstwa);

        if (Math.Abs(koniec.X - dotyk.Skad.X) > ProgPrzeciagniecia
            || Math.Abs(koniec.Y - dotyk.Skad.Y) > ProgPrzeciagniecia)
        {
            return;
        }

        // Z miejsca naciśnięcia, nie puszczenia: godzina ma być tą, w którą się trafiło,
        // a nie tą, na którą palec zjechał o trzy punkty.
        _kalendarz?.NewAt(dotyk.Dzien, dotyk.Skad.Y);
    }

    /// <summary>Przewijanie przejęło wskaźnik — to nie było dotknięcie siatki.</summary>
    private void SiatkaPorzucona(object? nadawca, PointerCaptureLostEventArgs e) =>
        _dotknieta = null;

    /// <summary>Szerokość kolumny godzin z lewej. Odpowiednik szerokości w XAML-u.</summary>
    private const double SlupekGodzin = 52;

    /// <summary>Zapas na suwak i odstęp między kolumnami.</summary>
    private const double Zapas = 14;

    private void NaZmianieSzerokosci(object? nadawca, SizeChangedEventArgs e) =>
        Szerokosc(e.NewSize.Width);

    /// <summary>
    /// Przesuwanie kreski bieżącej godziny.
    /// </summary>
    /// <remarks>
    /// Kreska liczy się przy składaniu siatki, więc przy otwartej aplikacji stała
    /// tam, gdzie wypadła przy wejściu na ekran — po kilku godzinach pokazywała
    /// godzinę sprzed kilku godzin i wyglądała jak błąd w strefie czasowej.
    /// Co minutę, bo częściej nie ma czego pokazywać: minuta to jeden punkt siatki.
    /// </remarks>
    private DispatcherTimer? _minutnik;

    /// <summary>Pasek z nazwami dni. Stoi nad siatką i jedzie z nią tylko w poziomie.</summary>
    private ScrollViewer? _naglowki;

    private void WireClock(MainViewModel model)
    {
        _minutnik ??= new DispatcherTimer(
            TimeSpan.FromMinutes(1),
            DispatcherPriority.Background,
            (_, _) =>
            {
                model.Calendar.Tick();

                // Przypomnienia razem z kreską „teraz": jedno i drugie jest odpowiedzią
                // na upływ czasu, a drugi minutnik na tę samą minutę byłby drugim
                // miejscem do zatrzymania i do zapomnienia o zatrzymaniu.
                _ = Probuj("Przypomnienia: sprawdzenie", model.CheckRemindersAsync);
            });

        _minutnik.Start();

        PodepnijPowrotDoOkna(model);
    }

    /// <summary>Czy powrót do okna jest już podsłuchiwany. Podpięcie idzie raz.</summary>
    private bool _powrotPodpiety;

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
    private void PodepnijPowrotDoOkna(MainViewModel model)
    {
        if (_powrotPodpiety || TopLevel.GetTopLevel(this) is not Window okno)
        {
            return;
        }

        _powrotPodpiety = true;

        okno.Activated += (_, _) =>
            _ = Probuj("Synchronizacja po powrocie", model.SynchronizujPoPowrocieAsync);
    }

    private void NaPrzewinieciuSiatki(object? nadawca, ScrollChangedEventArgs e)
    {
        if (_siatka is not null && _naglowki is not null)
        {
            _naglowki.Offset = new Vector(_siatka.Offset.X, 0);
        }
    }

    private void Szerokosc(double calosc)
    {
        if (calosc > 0)
        {
            _kalendarz?.SetAvailableWidth(calosc - SlupekGodzin - Zapas);
        }
    }

    private void NaProsbeOPrzewiniecie(double punkty)
    {
        _docelowe = punkty;
        Przewin();
    }

    private void NaUkladzie(object? nadawca, EventArgs e) => Przewin();

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
    private void Przewin()
    {
        if (_docelowe is not { } cel || _siatka is null || _siatka.Extent.Height <= 0)
        {
            return;
        }

        var zapas = Math.Max(0, _siatka.Extent.Height - _siatka.Viewport.Height);

        _siatka.Offset = new Vector(_siatka.Offset.X, Math.Clamp(cel, 0, zapas));
        _docelowe = null;
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
        if (DataContext is not MainViewModel model || TopLevel.GetTopLevel(this) is not { } okno)
        {
            return;
        }

        // Pierwsze rozstrzygnięcie układu: zdarzenie rozmiaru potrafi wypaść przed
        // podstawieniem modelu, a wtedy nie miałby go kto ustawić.
        model.IsNarrow = Bounds.Width > 0 && Bounds.Width < WidokWaski;

        WireCalendar(model);
        WireClock(model);

        model.Settings.SaveRequested = async nazwa =>
        {
            var plik = await okno.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Kopia zapasowa Marshala",
                SuggestedFileName = nazwa,
                DefaultExtension = "json",
                FileTypeChoices = [Json],
            });

            return plik is null ? null : await plik.OpenWriteAsync();
        };

        model.Settings.OpenRequested = async () =>
        {
            var pliki = await okno.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Wczytaj kopię Marshala",
                AllowMultiple = false,
                FileTypeFilter = [Json],
            });

            return pliki.Count == 0 ? null : await pliki[0].OpenReadAsync();
        };
    }

    private static FilePickerFileType Json => new("Kopia Marshala")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };
}
