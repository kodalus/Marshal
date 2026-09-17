using Avalonia;
using Marshal.Infrastructure.Notifications;
using Marshal.UI;

#if DYMKI
using DesktopNotifications;
using DesktopNotifications.Windows;
#endif

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
        PodepnijPowiadomienia();

        budowniczy.StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Podpięcie powiadomień systemowych. Tylko na Windowsie i tylko gdy się uda.
    /// </summary>
    /// <remarks>
    /// Warunek przy budowaniu, choć wolałabym inaczej. Typy od dymków istnieją tylko
    /// w windowsowej odmianie biblioteki, a ta wchodzi tylko pod windowsowym celem
    /// kompilacji — poza Windowsem nie ma czego wołać. Kosztem jest to, że CI, które
    /// chodzi na Linuksie, tych kilkunastu linii nie kompiluje; sprawdzenie systemu
    /// w środku zostaje, bo ten sam plik buduje się też pod zwykłym celem.
    /// </remarks>
    private static void PodepnijPowiadomienia()
    {
#if DYMKI
        if (!OperatingSystem.IsWindows())
        {
            InAppNotifier.StanSystemowych = "ten system nie ma dymków Windowsa";
            return;
        }

        try
        {
            var menedzer = new WindowsNotificationManager(
                WindowsApplicationContext.FromCurrentProcess("Marshal"));

            menedzer.Initialize().GetAwaiter().GetResult();
            InAppNotifier.StanSystemowych = "podpięte";

            // Drugi parametr to chwila wygaśnięcia i jest wymagany. Puste znaczy
            // „niech zostanie w centrum powiadomień" — przypomnienie, które znika samo
            // po minucie, jest bezużyteczne dokładnie wtedy, gdy się go nie widziało.
            InAppNotifier.Systemowe = (przypomnienie, _) =>
                menedzer.ShowNotification(
                    new Notification
                    {
                        Title = przypomnienie.Title,
                        Body = przypomnienie.Body ?? string.Empty,
                    },
                    expirationTime: null);
        }
        catch (Exception e)
        {
            // Bez powiadomień systemowych. Pasek w oknie zostaje i działa jak dotąd.
            InAppNotifier.StanSystemowych = $"{e.GetType().Name}: {e.Message}";
        }
#else
        InAppNotifier.StanSystemowych = "zbudowane bez obsługi dymków Windowsa";
#endif
    }

    /// <summary>Używane także przez podgląd projektanta Avalonii.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
