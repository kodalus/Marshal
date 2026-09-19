using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Marshal.Infrastructure.Notifications;

// Nazwa własna, bo „Notification" znaczy tu dwie różne rzeczy: naszą i androidową.
// „Application" też jest zajęte przez Android.App.Application, więc pełna ścieżka
// do naszego typu i tak by się nie skompilowała.
using Reminder = Marshal.Application.Abstractions.Notification;

namespace Marshal.Android;

/// <summary>
/// Powiadomienia systemowe Androida — ta sama rzecz, co dymki na Windowsie.
/// </summary>
/// <remarks>
/// <para>
/// Podpinane pod <see cref="InAppNotifier.Systemowe"/>, czyli dokładnie tam, gdzie
/// projekt pulpitu podpina swoje. Warstwa współdzielona nie wie o żadnym z nich
/// i wiedzieć nie ma: powiadomienie umie pokazać wyłącznie projekt platformy.
/// </para>
/// <para>
/// Pasek przypomnień w oknie zostaje i to nie jest zdublowanie. Powiadomienie odzywa
/// się, gdy patrzysz w co innego; pasek jest wtedy, gdy wrócisz — a na telefonie
/// odsunięte powiadomienie znika bezpowrotnie.
/// </para>
/// <para>
/// <b>Czego to nie daje.</b> Przypomnienia sprawdza minutnik w oknie, więc odzywają
/// się tylko wtedy, gdy aplikacja chodzi. Zamknięta nie odezwie się i nie udaje, że
/// umie: powiadomienia w tle to usługa pierwszoplanowa albo WorkManager, osobna
/// rzecz do zrobienia i osobna do sprawdzenia na sprzęcie.
/// </para>
/// </remarks>
internal static class Notifications
{
    /// <summary>Kanał. Jeden, bo aplikacja mówi o jednej rzeczy: że coś się zaczyna.</summary>
    private const string Channel = "przypomnienia";

    /// <summary>
    /// Kanał, zgoda i podpięcie. Wołane raz, przy starcie okna.
    /// </summary>
    /// <remarks>
    /// Wynik idzie do <see cref="InAppNotifier.StanSystemowych"/>, czyli do dziennika
    /// „Co się działo". Bez tego „nie ma powiadomienia" ma trzy przyczyny wyglądające
    /// identycznie: nie założył się kanał, nie ma zgody, albo nie było czego pokazać.
    /// Na telefonie bez kabla to jedyna droga, żeby je rozróżnić.
    /// </remarks>
    public static void Hook(Activity window)
    {
        Hook((Context)window);

        try
        {
            // Od Androida 13 na powiadomienia trzeba zgody, o którą pyta się raz.
            // Wcześniejsze wydania dają ją z instalacją. Tylko z okna — usługa ani
            // odbiornik nie mają jak o nic zapytać.
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                AskForConsent(window);
            }
        }
        catch (Exception e)
        {
            InAppNotifier.SystemStatus = $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>
    /// Kanał i podpięcie bez pytania o zgodę.
    /// </summary>
    /// <remarks>
    /// Osobno od wersji z oknem, bo budzik systemowy budzi proces **bez okna**:
    /// przypomnienie przy zamkniętej aplikacji powstaje w odbiorniku, a ten potrzebuje
    /// kanału i haczyka tak samo jak okno. O zgodę pytać wtedy nie ma jak i nie ma po co
    /// — bez niej i tak nic nie wyjdzie, a pytanie bez ekranu jest niewidoczne.
    /// </remarks>
    public static void Hook(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
            {
                InAppNotifier.SystemStatus = "system nie dał menedżera powiadomień";
                return;
            }

            manager.CreateNotificationChannel(
                new NotificationChannel(Channel, "Przypomnienia", NotificationImportance.Default)
                {
                    Description = "Zadania, o których Marshal ma się odezwać.",
                });

            InAppNotifier.SystemSink = (reminder, _) =>
            {
                Show(context, manager, reminder);
                return Task.CompletedTask;
            };

            InAppNotifier.SystemStatus = "podpięte";
        }
        catch (Exception e)
        {
            // Bez powiadomień systemowych. Pasek w oknie zostaje i działa jak dotąd.
            InAppNotifier.SystemStatus = $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>Prośba o zgodę na powiadomienia — Android 13 i nowsze.</summary>
    /// <remarks>
    /// Osobna metoda z adnotacją wydania, a nie warunek wpisany w miejscu użycia.
    /// Analizator zgodności platform czyta wyłącznie <c>OperatingSystem.IsAndroid…</c>
    /// i te adnotacje; porównanie <c>Build.VERSION.SdkInt</c> jest dla niego zwykłą
    /// liczbą, więc wołanie młodszego API zgłaszał jako błąd mimo poprawnego warunku.
    /// </remarks>
    [SupportedOSPlatform("android33.0")]
    private static void AskForConsent(Activity window)
    {
        if (window.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
            != global::Android.Content.PM.Permission.Granted)
        {
            window.RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 1);
        }
    }

    private static void Show(
        Context context,
        NotificationManager manager,
        Reminder reminder)
    {
        // Dotknięcie otwiera aplikację, a nie nic. SingleTop, więc wraca do okna,
        // które już stoi, zamiast zakładać drugie.
        var wejscie = new Intent(context, typeof(MainActivity));
        wejscie.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        var intent = PendingIntent.GetActivity(
            context, 0, wejscie,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var notification = new Notification.Builder(context, Channel)
            .SetContentTitle(reminder.Title)
            .SetContentText(reminder.Body ?? string.Empty)
            .SetSmallIcon(Resource.Drawable.notification_mark)
            .SetContentIntent(intent)
            .SetAutoCancel(true)
            .Build();

        // Identyfikator z zadania, nie kolejny numer: to samo zadanie ma podmieniać
        // swoje powiadomienie, a nie układać ich stos. Zgaszony bit znaku, a nie
        // wartość bezwzględna — ta na najmniejszej liczbie całkowitej rzuca wyjątkiem,
        // a skrót może ją zwrócić.
        manager.Notify(reminder.TaskId.GetHashCode() & 0x7FFFFFFF, notification);
    }
}
