using Android.App;
using Android.Content;
using Android.OS;
using Marshal.Application.UseCases;
using Marshal.Infrastructure.Notifications;
using Marshal.UI;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.Android;

/// <summary>
/// Budzik systemowy na najbliższe przypomnienie.
/// </summary>
/// <remarks>
/// <para>
/// Przy otwartej aplikacji przypomnień pilnuje minutnik w oknie. Zamknięta nie ma
/// minutnika i nie ma jak się odezwać — a przypomnienie, które działa tylko wtedy,
/// gdy się patrzy na aplikację, nie jest przypomnieniem.
/// </para>
/// <para>
/// Jeden budzik na **najbliższą** chwilę, a nie po jednym na każde przypomnienie.
/// Po każdym odezwaniu liczy się następną od nowa, więc lista zawsze jest aktualna,
/// a system trzyma jeden wpis zamiast setki.
/// </para>
/// <para>
/// Dokładny, jeśli wolno. Od Androida 12 dokładne budziki wymagają osobnej zgody,
/// której użytkownik może nie dać — wtedy zostaje niedokładny i przypomnienie potrafi
/// spóźnić się o kilkanaście minut. To jest gorsze od dokładnego i lepsze od żadnego,
/// więc aplikacja schodzi na niedokładny zamiast prosić o zgodę na siłę.
/// </para>
/// </remarks>
internal static class Budzik
{
    public const string Akcja = "com.kodalus.marshal.PRZYPOMNIENIE";

    private const int Numer = 7101;

    /// <summary>Przestawienie budzika na najbliższą chwilę. Wołane po każdej zmianie.</summary>
    public static async Task PrzestawAsync(Context kontekst)
    {
        try
        {
            await AppServices.ReadyAsync();

            var najblizsza = await AppServices.Provider
                .GetRequiredService<ReminderService>()
                .NajblizszaAsync();

            if (kontekst.GetSystemService(Context.AlarmService) is not AlarmManager zegar)
            {
                return;
            }

            var zamiar = Zamiar(kontekst);

            if (najblizsza is not { } chwila)
            {
                // Nic nie czeka — budzik skasowany, żeby system nie budził nas po nic.
                zegar.Cancel(zamiar);
                return;
            }

            var kiedy = chwila.ToUnixTimeMilliseconds();

            // „AllowWhileIdle", bo bez tego drzemka systemu przesuwa przypomnienia
            // ustawione na noc na rano — czyli dokładnie wtedy, gdy przestają być
            // potrzebne.
            if (Dokladny(zegar))
            {
                zegar.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, kiedy, zamiar);
            }
            else
            {
                zegar.SetAndAllowWhileIdle(AlarmType.RtcWakeup, kiedy, zamiar);
            }
        }
        catch (Exception e)
        {
            // Bez budzika przypomnienia działają po staremu, czyli przy otwartym oknie.
            InAppNotifier.StanSystemowych = $"budzik: {e.GetType().Name}: {e.Message}";
        }
    }

    private static bool Dokladny(AlarmManager zegar) =>
        !OperatingSystem.IsAndroidVersionAtLeast(31) || zegar.CanScheduleExactAlarms();

    private static PendingIntent Zamiar(Context kontekst)
    {
        var zamiar = new Intent(kontekst, typeof(OdbiorcaBudzika)).SetAction(Akcja);

        // „Immutable", bo nic w tym zamiarze nie ma być dopisywane z zewnątrz;
        // od Androida 12 jeden z tych dwóch znaczników jest zresztą wymagany.
        return PendingIntent.GetBroadcast(
            kontekst, Numer, zamiar,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }
}

/// <summary>
/// Odebranie budzika: pokazanie tego, co się należy, i nastawienie następnego.
/// </summary>
/// <remarks>
/// Wpis o starcie systemu też tutaj, bo budziki nie przeżywają wyłączenia telefonu.
/// Bez tego przypomnienia milkną po każdym restarcie, aż do następnego otwarcia
/// aplikacji — i nie widać, że zamilkły.
/// </remarks>
[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter([Intent.ActionBootCompleted])]
internal sealed class OdbiorcaBudzika : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        var kontekst = context.ApplicationContext ?? context;
        var oczekiwanie = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                await AppServices.ReadyAsync();

                // Powiadomienia podpinane tutaj, bo proces obudzony budzikiem nie ma
                // okna — a to okno je dotąd podpinało.
                Powiadomienia.Podepnij(kontekst);

                if (intent?.Action == Budzik.Akcja)
                {
                    await AppServices.Provider.GetRequiredService<ReminderService>().RunAsync();
                }

                await Budzik.PrzestawAsync(kontekst);
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
            finally
            {
                oczekiwanie?.Finish();
            }
        });
    }
}
