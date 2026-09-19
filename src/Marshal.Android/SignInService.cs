using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Marshal.Android;

/// <summary>
/// Usługa pierwszoplanowa na czas czekania na zgodę Google.
/// </summary>
/// <remarks>
/// <para>
/// Po co: kiedy przeglądarka przykryje aplikację, Android odkłada jej proces do
/// zamrażarki. Gniazdo nasłuchujące zostaje, więc jądro przyjmuje powrót z kodem
/// zgody — ale nie ma go komu obsłużyć i przeglądarka wisi zamiast się zamknąć.
/// Widać to było jako stronę, która nigdy się nie wczytuje, i trzeba było przepisywać
/// adres z jej paska ręcznie.
/// </para>
/// <para>
/// Proces z usługą pierwszoplanową nie jest „odłożony w tło" i zamrażarka go nie
/// rusza. Cena jest widoczna i taka ma być: przez te trzy minuty w pasku stanu wisi
/// powiadomienie. Powiadomienie o pracy, której użytkownik nie zlecał, byłoby
/// nachalne — to jest o pracy, na którą właśnie czeka.
/// </para>
/// <para>
/// Rodzaj <c>dataSync</c>, bo to jest dokładnie ta praca: logowanie jest pierwszym
/// krokiem synchronizacji. Usługa gaśnie razem z końcem czekania — udanym,
/// nieudanym albo przerwanym.
/// </para>
/// </remarks>
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
internal sealed class SignInService : Service
{
    private const string Channel = "logowanie";
    private const int Number = 7001;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(
        Intent? intent, StartCommandFlags flags, int startId)
    {
        if (GetSystemService(NotificationService) is NotificationManager manager)
        {
            // Niska waga: to jest znak życia, a nie wiadomość. Dźwięk przy czymś,
            // co użytkownik właśnie sam uruchomił, byłby hałasem.
            manager.CreateNotificationChannel(
                new NotificationChannel(Channel, "Logowanie", NotificationImportance.Low)
                {
                    Description = "Widoczne tylko wtedy, gdy Marshal czeka na zgodę Google.",
                });
        }

        var notification = new Notification.Builder(this, Channel)
            .SetContentTitle("Marshal — logowanie")
            .SetContentText("Czekam na zgodę w przeglądarce.")
            .SetSmallIcon(Resource.Drawable.notification_mark)
            .SetOngoing(true)
            .Build();

        StartForeground(Number, notification, ForegroundService.TypeDataSync);

        // Nie wskrzeszamy po zabiciu: zgoda, która przepadła razem z procesem,
        // i tak wymaga zaczęcia od nowa, a wskrzeszona usługa wisiałaby bez powodu.
        return StartCommandResult.NotSticky;
    }

    /// <summary>Zapalenie i zgaszenie usługi. Wołane z odbiorcy kodu zgody.</summary>
    public static void Watch(Context context, bool enable)
    {
        var intent = new Intent(context, typeof(SignInService));

        try
        {
            if (enable)
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StopService(intent);
            }
        }
        catch (Exception)
        {
            // Bez usługi logowanie dalej działa — tyle że przeglądarka może nie wrócić
            // sama i zostaje wklejenie adresu. Wywrócenie się na tym byłoby zamianą
            // niedogodności na awarię.
        }
    }
}
