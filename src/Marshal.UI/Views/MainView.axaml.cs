using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Marshal.Application.Review;
using Marshal.Application.UseCases;
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
        DataContextChanged += (_, _) => WirePicker();

        // Klawisze łapane w drodze w dół: inaczej kontrolka pod kursorem zjada
        // zdarzenie, zanim okno zdąży cokolwiek z nim zrobić.
        //
        // Kółka **nie** łapiemy. Przekierowanie go do okna szczegółu odbierało obrót
        // rozwiniętej liście godzin, czyli psuło wybieranie godziny — a zamknięte pole
        // daty ani godziny kółka nie zjada, więc przewijanie okna działa samo.
        AddHandler(KeyDownEvent, NaKlawiszu, RoutingStrategies.Tunnel);
        AddHandler(ContextRequestedEvent, NaMenu, RoutingStrategies.Bubble);

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
            || this.FindControl<TextBlock>("PodgladKiedy") is not { } kiedy
            || Cel(e) is not var (dzien, wysokosc))
        {
            return;
        }

        var pora = CalendarViewModel.Pora(wysokosc);

        tytul.Text = slot.Title;
        kiedy.Text = $"{dzien:dd.MM} · {pora:HH}:{pora:mm}";

        var gdzie = e.GetPosition(this);
        Canvas.SetLeft(podglad, gdzie.X + 14);
        Canvas.SetTop(podglad, gdzie.Y + 14);

        podglad.IsVisible = true;
    }

    private void SchowajPodglad()
    {
        if (this.FindControl<Border>("Podglad") is { } podglad)
        {
            podglad.IsVisible = false;
        }
    }

    private void BlokRuszony(object? nadawca, PointerEventArgs e)
    {
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
        if (nadawca is Control blok)
        {
            blok.Opacity = 1;
            e.Pointer.Capture(null);
        }

        SchowajPodglad();

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

        if (Cel(e) is not var (dzien, wysokosc) || _kalendarz is null)
        {
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
    private void OdhaczNaSiatce(object? nadawca, PointerPressedEventArgs e)
    {
        e.Handled = true;

        if (nadawca is not Control kwadracik
            || kwadracik.Tag is not SlotBox blok
            || _kalendarz is null)
        {
            return;
        }

        // Jedno polecenie na oba rodzaje bloku i oba kierunki. Okno nie musi wiedzieć,
        // czy pod spodem idzie zapis do bazy, czy zmiana nazwy w cudzym kalendarzu.
        _ = Probuj("Kalendarz: kwadracik", () => _kalendarz.ToggleCommand.ExecuteAsync(blok));
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

        // Blok na siatce niesie sam identyfikator, nie całe zadanie — trzeba je dobrać.
        if (Blok(zrodlo) is { TaskId: { } identyfikator })
        {
            e.Handled = true;

            _ = Probuj("Menu: otwarcie", async () =>
            {
                if (await model.FindTaskAsync(identyfikator) is { } zBazy)
                {
                    await PokazMenuAsync(model, zrodlo, zBazy);
                }
            });
        }
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

        var menu = new MenuFlyout
        {
            ItemsSource = new object[]
            {
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
                Pozycja("Weź na dziś", () => model.FocusTaskAsync(zadanie)),
                Pozycja("Pokaż w kalendarzu", () => model.ShowInCalendarAsync(zadanie)),
                Pozycja("Zamień na notatkę", () => model.ToNoteAsync(zadanie)),
                new Separator(),
                Pozycja("Usuń", () => model.TrashTaskAsync(zadanie)),
            },
        };

        menu.ShowAt(zrodlo, showAtPointer: true);
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
    /// Menu wiersza „Projektów”: barwa i usunięcie.
    /// </summary>
    /// <remarks>
    /// Powód odmowy sprawdzany **przed** pokazaniem menu, więc pozycja „Usuń” albo
    /// działa, albo mówi napisem, czego brakuje. Pozycja, która po kliknięciu nic nie
    /// robi, uczy nieufności do całego menu.
    /// </remarks>
    private async Task PokazMenuProjektuAsync(MainViewModel model, Control zrodlo, ProjectTreeRow wiersz)
    {
        var przeszkoda = await model.WhyCannotDeleteAsync(wiersz);

        var pozycje = new List<object>
        {
            Pozycja("Barwa…", () =>
            {
                PokazPalete(model, zrodlo, wiersz);
                return Task.CompletedTask;
            }),
        };

        if (!wiersz.IsArea)
        {
            pozycje.Add(new Separator());
            pozycje.Add(przeszkoda is null
                ? Pozycja("Usuń projekt", () => model.DeleteProjectAsync(wiersz))
                : new MenuItem { Header = przeszkoda, IsEnabled = false });
        }

        new MenuFlyout { ItemsSource = pozycje }.ShowAt(zrodlo, showAtPointer: true);
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
