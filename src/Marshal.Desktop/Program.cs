using Avalonia;
using DesktopNotifications;
using DesktopNotifications.Avalonia;
using Marshal.Infrastructure.Notifications;
using Marshal.UI;

namespace Marshal.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var budowniczy = BuildAvaloniaApp();

        // Powiadomienia systemowe podpinane **przed** startem i z pełnym
        // zabezpieczeniem. Toast to jedyny kawałek aplikacji, który zależy od rzeczy
        // spoza niej: na Windowsie od skrótu w menu Start, na Linuksie od usługi
        // powiadomień. Gdy któregoś zabraknie, aplikacja ma wstać tak samo — z paskiem
        // przypomnień w oknie, który działał do dziś.
        try
        {
            budowniczy = budowniczy.SetupDesktopNotifications(out var menedzer);

            if (menedzer is not null)
            {
                InAppNotifier.Systemowe = (przypomnienie, ct) =>
                    menedzer.ShowNotification(new Notification
                    {
                        Title = przypomnienie.Title,
                        Body = przypomnienie.Body ?? string.Empty,
                    });
            }
        }
        catch (Exception)
        {
            // Bez powiadomień systemowych. Pasek w oknie zostaje.
        }

        budowniczy.StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Używane także przez podgląd projektanta Avalonii.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
