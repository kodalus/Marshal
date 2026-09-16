using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Marshal.Infrastructure;
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
        var viewModel = services.GetRequiredService<MainViewModel>();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = viewModel };
                break;

            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = viewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();

        // Migracje, obszary początkowe i pierwsze wczytanie skrzynki dzieją się po
        // pokazaniu okna. Inaczej pierwsze uruchomienie wyglądałoby jak zawieszenie:
        // zakładanie bazy to ułamek sekundy, ale na słabszym telefonie widoczny.
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                await DependencyInjection.PrepareAsync(services);
                await viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                throw;
            }
        });
    }
}
