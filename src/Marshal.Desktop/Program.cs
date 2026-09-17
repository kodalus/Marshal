using Avalonia;
using DesktopNotifications;
using DesktopNotifications.Windows;
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
        PodepnijPowiadomienia();

        budowniczy.StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Podpięcie powiadomień systemowych. Tylko na Windowsie i tylko gdy się uda.
    /// </summary>
    /// <remarks>
    /// Sprawdzenie systemu w czasie działania, a nie warunek przy budowaniu: projekt
    /// buduje się na Linuksie (tam chodzi CI), więc warunek budowania znaczyłby, że ten
    /// kod nie jest kompilowany w ogóle i pierwsze sprawdzenie odbywałoby się na żywym
    /// Windowsie. Tak przynajmniej kompilator go widzi.
    /// </remarks>
    private static void PodepnijPowiadomienia()
    {
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
            // Windows odmawia dymków aplikacjom bez skrótu w menu Start — to jest
            // najczęstszy powód i nie jest usterką aplikacji, ale trzeba to napisać,
            // bo z samego braku dymka nie da się tego poznać.
            InAppNotifier.StanSystemowych = $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>Używane także przez podgląd projektanta Avalonii.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
