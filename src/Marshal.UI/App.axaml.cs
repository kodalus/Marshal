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
    public static string? SladPlatformy { get; set; }

    /// <summary>Co okno umie zrobić na prośbę z zewnątrz. Puste, dopóki okna nie ma.</summary>
    private static Action? _pokazKalendarz;

    /// <summary>Czy ktoś prosił o kalendarz, zanim było komu.</summary>
    private static bool _zadanoKalendarza;

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
    public static void PoprosOKalendarz()
    {
        if (_pokazKalendarz is { } teraz)
        {
            teraz();
            return;
        }

        _zadanoKalendarza = true;
    }

    /// <summary>Podpięcie okna. Spełnia prośbę, która przyszła, zanim okno powstało.</summary>
    private static void PodepnijKalendarz(Action pokaz)
    {
        _pokazKalendarz = pokaz;

        if (!_zadanoKalendarza)
        {
            return;
        }

        _zadanoKalendarza = false;
        pokaz();
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Przywrócenie okna ze znaczka w zasobniku.
    /// </summary>
    /// <remarks>
    /// Zwinięta aplikacja bez drogi powrotu wygląda na zawieszoną, a znaczek, który
    /// nic nie robi po kliknięciu, jest gorszy od jego braku.
    /// </remarks>
    private void PokazOkno(object? nadawca, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } okno })
        {
            okno.Show();
            okno.WindowState = WindowState.Normal;
            okno.Activate();
        }
    }

    private void Zakoncz(object? nadawca, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime pulpit)
        {
            pulpit.Shutdown();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = AppServices.Build();

        // Okno powstaje **puste**, a model widoku dochodzi dopiero po przygotowaniu
        // bazy. Rozwiązanie modelu wciąga cały graf zależności, a ten sięga po
        // tożsamość urządzenia i zapisany znacznik zegara — czyli po tabele, których
        // przed migracją nie ma. Wcześniej działo się to przed PrepareAsync i kończyło
        // wyjątkiem z konstruktora okna: białe tło i natychmiastowe zamknięcie,
        // na obu platformach, przy każdym uruchomieniu.
        var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var singleView = ApplicationLifetime as ISingleViewApplicationLifetime;

        var okno = desktop is null ? null : new MainWindow();
        var widok = singleView is null ? null : new MainView();

        if (desktop is not null)
        {
            desktop.MainWindow = okno;
        }

        if (singleView is not null)
        {
            singleView.MainView = widok;
        }

        base.OnFrameworkInitializationCompleted();

        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                await AppServices.ReadyAsync();

                var viewModel = services.GetRequiredService<MainViewModel>();

                // Motyw przestawia aplikacja, bo dotyczy całego okna, a nie ekranu
                // ustawień. Zapisany motyw leży w bazie, więc dopiero teraz.
                viewModel.Settings.ThemeChanged += (_, wybor) =>
                    RequestedThemeVariant = Variant(wybor);

                RequestedThemeVariant = Variant(
                    services.GetRequiredService<ISettings>().Theme);

                if (okno is not null)
                {
                    okno.DataContext = viewModel;
                }

                if (widok is not null)
                {
                    widok.DataContext = viewModel;
                }

                await viewModel.InitializeAsync();

                PodepnijKalendarz(
                    () => Dispatcher.UIThread.Post(
                        () => viewModel.ShowCalendarCommand.Execute(null)));

                // Strefa w dzienniku przy każdym starcie: przesuwa wszystkie godziny
                // naraz, a przesunięte wszystko wygląda tak samo jak źle pobrane dane.
                var ustawienia = services.GetRequiredService<ISettings>();
                var zegar = services.GetRequiredService<IClock>();

                await services.GetRequiredService<IActivityLog>().RecordAsync(
                    "Start",
                    $"strefa {ustawienia.Zone.Id}, teraz {zegar.Now:yyyy-MM-dd HH:mm zzz}, "
                        + $"wydanie {Wydanie()}, "
                        + $"powiadomienia systemowe: {InAppNotifier.StanSystemowych}, "
                        + $"kalendarz główny: {ustawienia.MainCalendarId?.ToString() ?? "nieustawiony"}",
                    ustawienia.ZoneProblem is null && SladPlatformy is null
                        ? ActivityLevel.Ok : ActivityLevel.Problem,
                    string.Join("\n\n", new[] { ustawienia.ZoneProblem, SladPlatformy }
                        .Where(w => !string.IsNullOrWhiteSpace(w))) is { Length: > 0 } szczegoly
                        ? szczegoly : null);
            }
            catch (Exception ex)
            {
                // **Nie rzucamy dalej.** Wyjątek z async void zabija proces, a jedynym
                // objawem jest zniknięcie okna — bez śladu, do którego da się dojść
                // bez kabla. Awaria startu ma być widoczna na ekranie, bo tylko wtedy
                // da się ją zgłosić.
                System.Diagnostics.Debug.WriteLine(ex);

                // Do dziennika też, o ile baza w ogóle stoi — a przy awarii startu
                // bardzo często nie stoi, więc ekran awarii zostaje jedyną drogą
                // i dlatego nie zależy ani od bazy, ani od powiązań.
                await services.GetRequiredService<IActivityLog>().RecordAsync(
                    "Start", "nie udało się", ActivityLevel.Problem, ex.ToString());

                var awaria = StartupFailure.Build(ex);

                if (okno is not null)
                {
                    okno.Content = awaria;
                }

                if (widok is not null)
                {
                    widok.Content = awaria;
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
    private static string Wydanie()
    {
        try
        {
            var plik = typeof(App).Assembly.Location;

            return string.IsNullOrEmpty(plik)
                ? "nieznane"
                : File.GetLastWriteTime(plik).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "nieznane";
        }
    }

    private static ThemeVariant Variant(ThemeChoice wybor) => wybor switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,

        // „Za systemem" to Default, a nie odgadywanie jasności z zegara. System wie,
        // czy użytkownik ma włączony tryb nocny; aplikacja nie ma jak tego zgadnąć.
        _ => ThemeVariant.Default,
    };
}
