using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;
using Marshal.Infrastructure.Notifications;
using Marshal.UI.ViewModels;
using Marshal.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.UI;

// Typ bazowy kwalifikowany pełną nazwą celowo. W przestrzeni Marshal.UI sama
// „Application" rozwiązuje się do przestrzeni nazw Marshal.Application — szukanie
// idzie Marshal.UI → Marshal → globalnie i zatrzymuje się na naszej warstwie
// aplikacji, nigdy nie docierając do typu Avalonii (CS0118).
public partial class App : Avalonia.Application
{
    /// <summary>
    /// Co platforma chce powiedzieć przy starcie. Puste, gdy nie ma nic.
    /// </summary>
    /// <remarks>
    /// Wpis „Start" w dzienniku powstaje tutaj, w warstwie współdzielonej, a rzeczy
    /// warte odnotowania bywają po stronie platformy — na przykład to, że poprzednie
    /// uruchomienie padło. Warstwa współdzielona nie ma jak ich zapytać, więc to one
    /// zostawiają tu zdanie przed startem.
    /// </remarks>
    public static string? PlatformTrace { get; set; }

    /// <summary>Co okno umie zrobić na prośbę z zewnątrz. Puste, dopóki okna nie ma.</summary>
    private static Action<CalendarRequest?>? _showCalendar;

    /// <summary>Czy ktoś prosił o kalendarz, zanim było komu.</summary>
    private static bool _calendarAsked;

    /// <summary>Zadanie, o które proszono razem z kalendarzem. Puste, gdy o żadne.</summary>
    private static CalendarRequest? _askedTask;

    /// <summary>
    /// Prośba spoza okna, żeby pokazać kalendarz — z widgetu na ekranie domowym.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prośba, nie polecenie, i dlatego przez zapamiętanie zamiast wołania wprost.
    /// Widget dotknięty przy zamkniętej aplikacji budzi ją od zera: w tej chwili okna
    /// jeszcze nie ma i nie ma komu niczego pokazać, a kilkaset milisekund później
    /// już jest. Zapamiętana prośba obsługuje obie te chwile jednym zdaniem.
    /// </para>
    /// <para>
    /// Zerowana po spełnieniu, bo inaczej każde następne otwarcie aplikacji —
    /// z ikony, z powiadomienia, skądkolwiek — przerzucałoby na kalendarz.
    /// </para>
    /// </remarks>
    /// <param name="what">
    /// Zadanie albo wydarzenie do otwarcia razem z kalendarzem — albo nic. Dotknięcie
    /// kafelka prowadzi na kalendarz, a dotknięcie pozycji na nim — do tej jednej rzeczy,
    /// o którą chodziło. Bez tego z widgetu dało się wejść tylko „gdzieś w okolice"
    /// i dalej trzeba było szukać wzrokiem po siatce.
    /// </param>
    public static void AskForCalendar(CalendarRequest? what = null)
    {
        if (_showCalendar is { } now)
        {
            now(what);
            return;
        }

        _calendarAsked = true;
        _askedTask = what;
    }

    /// <summary>Podpięcie okna. Spełnia prośbę, która przyszła, zanim okno powstało.</summary>
    private static void HookCalendar(Action<CalendarRequest?> show)
    {
        _showCalendar = show;

        if (!_calendarAsked)
        {
            return;
        }

        _calendarAsked = false;

        var what = _askedTask;
        _askedTask = null;

        show(what);
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Przywrócenie okna ze znaczka w zasobniku.
    /// </summary>
    /// <remarks>
    /// Zwinięta aplikacja bez drogi powrotu wygląda na zawieszoną, a znaczek, który
    /// nic nie robi po kliknięciu, jest gorszy od jego braku.
    /// </remarks>
    private void ShowWindow(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    private void Finish(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Bicie serca wątku okna — na tym samym miejscu w kolejce, co obsługa dotknięć,
        // bo to właśnie ją Android mierzy, pokazując „aplikacja nie odpowiada".
        // Zatrzymywane po wpisie startowym: dalej mierzy już zwykłą pracę aplikacji,
        // a pytanie dotyczy rozruchu.
        var heartbeat = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100), DispatcherPriority.Input, (_, _) => Startup.Beat());

        heartbeat.Start();

        var services = AppServices.Build();

        // Okno powstaje **puste**, a model widoku dochodzi dopiero po przygotowaniu
        // bazy. Rozwiązanie modelu wciąga cały graf zależności, a ten sięga po
        // tożsamość urządzenia i zapisany znacznik zegara — czyli po tabele, których
        // przed migracją nie ma. Wcześniej działo się to przed PrepareAsync i kończyło
        // wyjątkiem z konstruktora okna: białe tło i natychmiastowe zamknięcie,
        // na obu platformach, przy każdym uruchomieniu.
        var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var singleView = ApplicationLifetime as ISingleViewApplicationLifetime;

        var window = desktop is null ? null : new MainWindow();
        var view = singleView is null ? null : new MainView();

        if (desktop is not null)
        {
            desktop.MainWindow = window;
        }

        if (singleView is not null)
        {
            singleView.MainView = view;
        }

        base.OnFrameworkInitializationCompleted();

        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                // Przeniesienie przygotowania na wątek z puli siedzi w ReadyAsync,
                // a nie tutaj — bo okno nie jest jedynym, kto tam wchodzi, ani nawet
                // pierwszym. Stało tu kiedyś Task.Run i nie pomagało: budzik przypomnień
                // budzony w OnCreate sięgał po to samo zadanie wcześniej, z wątku okna,
                // i to on wykonywał całą migrację.
                //
                // Ta liczba mierzy więc **czekanie**, nie pracę. Przy pierwszym starcie
                // po zmianie schematu bazy będzie duża i to jest w porządku: w tym czasie
                // okno jest już narysowane i odpowiada.
                // Chwila, w której wątek okna w ogóle doszedł do wczytywania. Między
                // końcem tworzenia okna a tym miejscem stoi kolejka zdarzeń wątku okna
                // — a jeśli coś ją zapycha, widać to wyłącznie jako tę różnicę.
                Startup.Model = Startup.Now();

                var startClock = Stopwatch.StartNew();

                await AppServices.ReadyAsync();

                var prepare = startClock.ElapsedMilliseconds;

                var viewModel = services.GetRequiredService<MainViewModel>();

                var merge = startClock.ElapsedMilliseconds - prepare;

                // Motyw przestawia aplikacja, bo dotyczy całego okna, a nie ekranu
                // ustawień. Zapisany motyw leży w bazie, więc dopiero teraz.
                viewModel.Settings.ThemeChanged += (_, choice) =>
                    RequestedThemeVariant = Variant(choice);

                RequestedThemeVariant = Variant(
                    services.GetRequiredService<ISettings>().Theme);

                if (window is not null)
                {
                    window.DataContext = viewModel;
                }

                if (view is not null)
                {
                    view.DataContext = viewModel;
                }

                await viewModel.InitializeAsync();

                var load = startClock.ElapsedMilliseconds - prepare - merge;

                HookCalendar(
                    what => Dispatcher.UIThread.Post(
                        () => viewModel.ShowCalendar(what)));

                // Strefa w dzienniku przy każdym starcie: przesuwa wszystkie godziny
                // naraz, a przesunięte wszystko wygląda tak samo jak źle pobrane dane.
                var settings = services.GetRequiredService<ISettings>();
                var clock = services.GetRequiredService<IClock>();

                // Serce zatrzymane przed spisaniem wpisu, żeby wpisana przerwa dotyczyła
                // rozruchu, a nie tego, co dzieje się po nim.
                heartbeat.Stop();

                await services.GetRequiredService<IActivityLog>().RecordAsync(
                    "Start",
                    $"strefa {settings.Zone.Id}, teraz {clock.Now:yyyy-MM-dd HH:mm zzz}, "
                        + $"wydanie {Release()}, "
                        + $"powiadomienia systemowe: {InAppNotifier.SystemStatus}, "
                        + $"kalendarz główny: {settings.MainCalendarId?.ToString() ?? "nieustawiony"}, "

                        // Czasy startu w dzienniku, bo „aplikacja się zawiesza przy
                        // otwarciu" nie mówi, co ją trzyma — a trzy liczby mówią.
                        + $"start: przygotowanie {prepare} ms, "
                        + $"złożenie {merge} ms, wczytanie {load} ms"
                        + (Startup.Measured ? $", {Startup.Description}" : string.Empty),
                    settings.ZoneProblem is null && PlatformTrace is null
                        ? ActivityLevel.Ok : ActivityLevel.Problem,
                    string.Join("\n\n", new[] { settings.ZoneProblem, PlatformTrace }
                        .Where(w => !string.IsNullOrWhiteSpace(w))) is { Length: > 0 } details
                        ? details : null);
            }
            catch (Exception ex)
            {
                // **Nie rzucamy dalej.** Wyjątek z async void zabija proces, a jedynym
                // objawem jest zniknięcie okna — bez śladu, do którego da się dojść
                // bez kabla. Awaria startu ma być widoczna na ekranie, bo tylko wtedy
                // da się ją zgłosić.
                Debug.WriteLine(ex);

                // Do dziennika też, o ile baza w ogóle stoi — a przy awarii startu
                // bardzo często nie stoi, więc ekran awarii zostaje jedyną drogą
                // i dlatego nie zależy ani od bazy, ani od powiązań.
                await services.GetRequiredService<IActivityLog>().RecordAsync(
                    "Start", "nie udało się", ActivityLevel.Problem, ex.ToString());

                var failure = StartupFailure.Build(ex);

                if (window is not null)
                {
                    window.Content = failure;
                }

                if (view is not null)
                {
                    view.Content = failure;
                }
            }
        });
    }

    /// <summary>
    /// Kiedy zbudowano to, co właśnie działa.
    /// </summary>
    /// <remarks>
    /// Połowa dzisiejszych rozmów utknęła na pytaniu, którego nie dało się rozstrzygnąć:
    /// czy uruchomiona aplikacja zawiera poprawkę sprzed dziesięciu minut, czy jeszcze
    /// nie. „Nie ma wpisu w dzienniku" znaczy co innego w wersji, która tych wpisów
    /// jeszcze nie robi. Data zbudowania pliku rozstrzyga to jedną linijką.
    /// </remarks>
    /// <summary>
    /// Które to wydanie — z wersji informacyjnej zestawu.
    /// </summary>
    /// <remarks>
    /// Była tu data pliku aplikacji i na Androidzie nie działała wcale: pliku pod tą
    /// ścieżką nie ma, a data nieistniejącego pliku to zero kalendarza Windows. W
    /// dzienniku stało przez to „wydanie 1601-01-01 01:24" — czyli dokładnie w miejscu,
    /// w którym ma być widać, co chodzi na telefonie, stała informacja, że nie wiadomo.
    ///
    /// Wersja informacyjna jedzie w zestawie, więc jest na każdej platformie taka sama
    /// i nie zależy od tego, czy cokolwiek leży na dysku. CI dokleja do niej skrót
    /// zapisu (zob. Directory.Build.props), więc z tej linijki da się trafić w commit.
    /// </remarks>
    private static string Release()
    {
        var version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return string.IsNullOrWhiteSpace(version) ? "nieznane" : version;
    }

    private static ThemeVariant Variant(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,

        // „Za systemem" to Default, a nie odgadywanie jasności z zegara. System wie,
        // czy użytkownik ma włączony tryb nocny; aplikacja nie ma jak tego zgadnąć.
        _ => ThemeVariant.Default,
    };
}
