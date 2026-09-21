using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Marshal.Application.UseCases;
using Marshal.Infrastructure.Notifications;
using Marshal.UI;
using Microsoft.Extensions.DependencyInjection;

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
/// Podpinane pod <see cref="InAppNotifier.SystemSink"/>, czyli dokładnie tam, gdzie
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
    /// Wynik idzie do <see cref="InAppNotifier.SystemStatus"/>, czyli do dziennika
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

    /// <summary>
    /// Numer powiadomienia z zadania, nie kolejny z licznika.
    /// </summary>
    /// <remarks>
    /// To samo zadanie ma podmieniać swoje powiadomienie, a nie układać ich stos —
    /// i po tym samym numerze trzeba je potem zgasić. Zgaszony bit znaku, a nie wartość
    /// bezwzględna: ta na najmniejszej liczbie całkowitej rzuca wyjątkiem, a skrót
    /// może ją zwrócić.
    /// </remarks>
    internal static int Number(Guid task) => task.GetHashCode() & 0x7FFFFFFF;

    /// <summary>Zgaszenie powiadomienia zadania, gdy nie ma już o czym przypominać.</summary>
    internal static void Dismiss(Context context, Guid task)
    {
        if (context.GetSystemService(Context.NotificationService) is NotificationManager manager)
        {
            manager.Cancel(Number(task));
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
            .AddAction(Done(context, reminder.TaskId))
            .SetAutoCancel(true)
            .Build();

        manager.Notify(Number(reminder.TaskId), notification);
    }

    /// <summary>
    /// Przycisk „Zakończ" pod treścią powiadomienia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Przypomnienie odzywa się w chwili, w której coś się zaczyna — a odpowiedź na nie
    /// bywa taka, że rzecz jest już zrobiona. Bez tego przycisku jedyną drogą jest
    /// otwarcie aplikacji, odnalezienie zadania i odhaczenie go tam: trzy czynności
    /// na odpowiedź, która brzmi „zrobione".
    /// </para>
    /// <para>
    /// Rozgłoszenie, nie okno: odhaczenie ma się wydarzyć <b>bez</b> otwierania
    /// aplikacji. Numer zgłoszenia z zadania, bo dwa przypomnienia naraz muszą nieść
    /// dwa różne zadania — wspólny numer znaczyłby, że drugie nadpisuje pierwszemu
    /// jego dodatki i oba odhaczają to samo.
    /// </para>
    /// </remarks>
    private static Notification.Action Done(Context context, Guid task)
    {
        var intent = new Intent(context, typeof(ReminderDone));
        intent.SetAction(ReminderDone.DoneAction);
        intent.PutExtra(ReminderDone.TaskExtra, task.ToString());

        var doing = PendingIntent.GetBroadcast(
            context, Number(task), intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new Notification.Action.Builder(
            global::Android.Graphics.Drawables.Icon.CreateWithResource(
                context, Resource.Drawable.notification_mark),
            "Zakończ",
            doing).Build();
    }
}

/// <summary>
/// „Zakończ" dotknięte w powiadomieniu.
/// </summary>
/// <remarks>
/// <para>
/// Niewystawiony: zamiar oczekujący składamy sami i sami wskazujemy w nim tę klasę,
/// więc nie ma powodu, żeby dało się ją zawołać z zewnątrz. Odhaczenie cudzego zadania
/// przez rozgłoszenie byłoby cichym zapisem do bazy z dowolnej aplikacji na telefonie.
/// </para>
/// <para>
/// <b>Najpierw zapis, potem zgaszenie powiadomienia.</b> Odwrotna kolejność wygląda
/// żwawiej i kłamie: nieudany zapis zostawiałby zadanie nieodhaczone i bez przypomnienia,
/// czyli bez jedynego śladu, że w ogóle było. Dopóki zapis się nie uda, powiadomienie
/// zostaje na ekranie i da się spróbować drugi raz.
/// </para>
/// </remarks>
[BroadcastReceiver(Enabled = true, Exported = false)]
internal sealed class ReminderDone : BroadcastReceiver
{
    /// <summary>
    /// Własny napis, nie ten od budzika przypomnień.
    /// </summary>
    /// <remarks>
    /// Oba zamiary wskazują swoją klasę wprost, więc wspólny napis działał — i był
    /// pułapką: dwie różne rzeczy pod jedną nazwą rozchodzą się dopiero wtedy, gdy
    /// ktoś doda trzecią i zacznie rozróżniać po akcji.
    /// </remarks>
    internal const string DoneAction = "com.kodalus.marshal.ZAKONCZENIE";

    internal const string TaskExtra = "zadanie";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != DoneAction)
        {
            return;
        }

        var app = context.ApplicationContext ?? context;
        var id = intent.GetStringExtra(TaskExtra);
        var waiting = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                if (!Guid.TryParse(id, out var task))
                {
                    return;
                }

                // Haczyk na dymki przed składaniem, tak samo jak w widgecie: składanie
                // nadrabia zaległe przypomnienia, a ten odbiornik potrafi być pierwszy
                // w procesie i jedyny.
                Notifications.Hook(app);

                await AppServices.ReadyAsync();

                var done = await AppServices.Provider
                    .GetRequiredService<TaskEditService>()
                    .CompleteAsync(task);

                Notifications.Dismiss(app, task);

                // Kafelek pokazuje ten sam plan dnia, z którego zadanie właśnie zeszło.
                TodayWidget.Refresh(app);

                await Alarm.Save(
                    "Przypomnienie: zakończone z powiadomienia",
                    done?.Title ?? "zadania już nie ma");
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());

                await Alarm.Save("Przypomnienie: zakończenie z powiadomienia", "nie udało się", e);
            }
            finally
            {
                waiting?.Finish();
            }
        });
    }
}
