using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;
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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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

                // Strefa w dzienniku przy każdym starcie: przesuwa wszystkie godziny
                // naraz, a przesunięte wszystko wygląda tak samo jak źle pobrane dane.
                var ustawienia = services.GetRequiredService<ISettings>();
                var zegar = services.GetRequiredService<IClock>();

                await services.GetRequiredService<IActivityLog>().RecordAsync(
                    "Start",
                    $"strefa {ustawienia.Zone.Id}, teraz {zegar.Now:yyyy-MM-dd HH:mm zzz}",
                    ustawienia.ZoneProblem is null ? ActivityLevel.Ok : ActivityLevel.Problem,
                    ustawienia.ZoneProblem);
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

    private static ThemeVariant Variant(ThemeChoice wybor) => wybor switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,

        // „Za systemem" to Default, a nie odgadywanie jasności z zegara. System wie,
        // czy użytkownik ma włączony tryb nocny; aplikacja nie ma jak tego zgadnąć.
        _ => ThemeVariant.Default,
    };
}
