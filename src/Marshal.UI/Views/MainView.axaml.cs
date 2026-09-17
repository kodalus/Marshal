using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Marshal.Application.Review;
using Marshal.Application.UseCases;
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
        DataContextChanged += (_, _) => WirePicker();

        // Klawisze i kółko łapane w drodze w dół: inaczej kontrolka pod kursorem
        // zjada zdarzenie, zanim okno zdąży cokolwiek z nim zrobić.
        AddHandler(KeyDownEvent, NaKlawiszu, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, PrzewinSzczegol, RoutingStrategies.Tunnel);

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
        _kalendarz = model.Calendar;
        _szczegol = model.Detail;

        model.Calendar.ScrollRequested -= NaProsbeOPrzewiniecie;
        model.Calendar.ScrollRequested += NaProsbeOPrzewiniecie;

        if (_siatka is not null)
        {
            _siatka.LayoutUpdated -= NaUkladzie;
            _siatka.LayoutUpdated += NaUkladzie;
            _siatka.SizeChanged -= NaZmianieSzerokosci;
            _siatka.SizeChanged += NaZmianieSzerokosci;

            // Pierwsze podanie szerokości: zdarzenie rozmiaru potrafi wypaść przed
            // podstawieniem modelu, a wtedy siatka zostałaby na szerokości zapasowej.
            Szerokosc(_siatka.Bounds.Width);
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

    private void BlokWcisniety(object? nadawca, PointerPressedEventArgs e)
    {
        if (nadawca is Control blok && blok.Tag is SlotBox slot)
        {
            _wciesniety = slot;
            _skad = e.GetPosition(this);
            _przeciagam = false;
        }
    }

    private void BlokRuszony(object? nadawca, PointerEventArgs e)
    {
        var doRuszenia = _wciesniety is { TaskId: not null }
            || _wciesniety is { SourceId: not null, ExternalId: not null };

        if (!doRuszenia || _przeciagam)
        {
            return;
        }

        var teraz = e.GetPosition(this);

        if (Math.Abs(teraz.X - _skad.X) > ProgPrzeciagniecia
            || Math.Abs(teraz.Y - _skad.Y) > ProgPrzeciagniecia)
        {
            _przeciagam = true;

            if (nadawca is Control blok)
            {
                blok.Opacity = 0.5;
                e.Pointer.Capture(blok);
            }
        }
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
        if (nadawca is Control blok)
        {
            blok.Opacity = 1;
            e.Pointer.Capture(null);
        }

        var slot = _wciesniety;
        var przeciagniete = _przeciagam;

        _wciesniety = null;
        _przeciagam = false;

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

        if (this.FindControl<ItemsControl>("KolumnyDni") is not { } kolumny
            || _warstwaGodzin is null)
        {
            return;
        }

        var wKolumnach = e.GetPosition(kolumny);
        var szerokosc = _kalendarz.ColumnWidth + 2;
        var numer = Math.Clamp((int)(wKolumnach.X / szerokosc), 0, _kalendarz.VisibleDays - 1);

        var wysokosc = e.GetPosition(_warstwaGodzin).Y;

        var dzien = _kalendarz.Anchor.AddDays(numer);

        // Zadanie idzie naszą drogą, wydarzenie — prosto do kalendarza, z którego
        // pochodzi. To druga rzecz, nie ta sama z innym zapisem.
        _ = slot.TaskId is { } zadanie
            ? Probuj("Kalendarz: przełożenie", () => _kalendarz.MoveAsync(zadanie, dzien, wysokosc))
            : Probuj(
                "Kalendarz: przeniesienie wydarzenia",
                () => _kalendarz.MoveEventAsync(slot, dzien, wysokosc));
    }



    /// <summary>Kliknięcie w przyciemnione tło zamyka okno szczegółu.</summary>
    private void TloSzczegolu(object? nadawca, PointerPressedEventArgs e) => _szczegol?.Close();

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
        if (e.Key != Key.Escape || DataContext is not MainViewModel model)
        {
            return;
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
        else
        {
            return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Przewijanie okna szczegółu kółkiem, także nad polami daty i godziny.
    /// </summary>
    /// <remarks>
    /// Pola daty i godziny zjadają obrót kółka na własne potrzeby, więc kursor nad nimi
    /// zatrzymywał przewijanie całego okna — a są w środku listy, którą trzeba przewinąć.
    /// Zdarzenie łapane w drodze **w dół**, zanim dojdzie do pola.
    /// </remarks>
    private void PrzewinSzczegol(object? nadawca, PointerWheelEventArgs e)
    {
        if (this.FindControl<ScrollViewer>("SzczegolPrzewijanie") is not { } widok)
        {
            return;
        }

        if (e.Source is Control zrodlo
            && zrodlo.FindAncestorOfType<TimePicker>() is null
            && zrodlo.FindAncestorOfType<DatePicker>() is null
            && zrodlo.FindAncestorOfType<NumericUpDown>() is null)
        {
            return;
        }

        widok.Offset = widok.Offset.WithY(
            Math.Clamp(
                widok.Offset.Y - (e.Delta.Y * 50),
                0,
                Math.Max(0, widok.Extent.Height - widok.Viewport.Height)));

        e.Handled = true;
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
        if (DataContext is not MainViewModel model || nadawca is not ListBox lista)
        {
            return;
        }

        var zadanie = lista.SelectedItem switch
        {
            TaskItem wprost => wprost,
            TaskRow wiersz => wiersz.Task,
            WaitingItem czekajace => czekajace.Task,
            NowPick wybor => wybor.Task,
            _ => null,
        };

        if (zadanie is not null)
        {
            _ = Probuj("Lista: otwarcie zadania", () => model.Detail.LoadAsync(zadanie));
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

    /// <summary>
    /// Kliknięcie w pustą siatkę zakłada nową rzecz na tej godzinie.
    /// </summary>
    /// <remarks>
    /// Obsługiwane na warstwie linii godzin, nie na blokach: bloki są przyciskami
    /// i zjadają kliknięcie same, więc klik w zajęte miejsce nie trafia tutaj i nie
    /// zakłada niczego pod spodem. Dzień bierze się ze znacznika ustawionego w XAML-u,
    /// bo warstwa linii nie zna kolumny, na której leży.
    /// </remarks>
    private void NowaRzeczNaSiatce(object? nadawca, PointerPressedEventArgs e)
    {
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

        _kalendarz?.NewAt(dzien, e.GetPosition(warstwa).Y);
    }

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

    private void WireClock(MainViewModel model)
    {
        _minutnik ??= new DispatcherTimer(
            TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => model.Calendar.Tick());

        _minutnik.Start();
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
