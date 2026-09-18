using Android.App;
using Android.Content;
using Android.OS;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Diagnostics;
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

            var dziennik = AppServices.Provider.GetRequiredService<IActivityLog>();

            if (najblizsza is not { } chwila)
            {
                // Nic nie czeka — budzik skasowany, żeby system nie budził nas po nic.
                zegar.Cancel(zamiar);
                await dziennik.RecordAsync("Przypomnienia: budzik", "nic nie czeka");
                return;
            }

            var kiedy = chwila.ToUnixTimeMilliseconds();

            // „AllowWhileIdle", bo bez tego drzemka systemu przesuwa przypomnienia
            // ustawione na noc na rano — czyli dokładnie wtedy, gdy przestają być
            // potrzebne.
            var dokladny = Dokladny(zegar);

            if (dokladny)
            {
                zegar.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, kiedy, zamiar);
            }
            else
            {
                zegar.SetAndAllowWhileIdle(AlarmType.RtcWakeup, kiedy, zamiar);
            }

            // Ślad w dzienniku, bo przy zamkniętej aplikacji nie ma **żadnego** innego
            // sposobu, żeby odróżnić trzy rzeczy wyglądające tak samo: budzik nienastawiony,
            // nastawiony i niedostarczony przez system, dostarczony i bez czego pokazać.
            await dziennik.RecordAsync(
                "Przypomnienia: budzik",
                $"nastawiony na {chwila:yyyy-MM-dd HH:mm zzz}"
                    + (dokladny ? string.Empty : " (niedokładny — system nie dał zgody)"));
        }
        catch (Exception e)
        {
            // Bez budzika przypomnienia działają po staremu, czyli przy otwartym oknie.
            InAppNotifier.StanSystemowych = $"budzik: {e.GetType().Name}: {e.Message}";
            await Zapisz("Przypomnienia: budzik", "nie udało się nastawić", e);
        }
    }

    /// <summary>Wpis do dziennika, który nie wywraca wołającego, gdy baza nie stoi.</summary>
    public static async Task Zapisz(
        string co, string tresc, Exception? blad = null, ActivityLevel? poziom = null)
    {
        try
        {
            await AppServices.Provider.GetRequiredService<IActivityLog>().RecordAsync(
                co, tresc,
                poziom ?? (blad is null ? ActivityLevel.Ok : ActivityLevel.Problem),
                blad?.ToString());
        }
        catch
        {
            // Dziennik jest tu narzędziem do patrzenia, a nie częścią działania.
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
/// Niewystawiony na zewnątrz i tak być musi: budzik przychodzi zamiarem wskazującym
/// tę klasę wprost, a taki dociera również do odbiornika zamkniętego. Start systemu
/// jest osobną klasą, bo **tamten** musi być wystawiony — inaczej system nie ma go
/// jak zawołać — a wystawianie przy okazji odbiornika przypomnień dałoby każdej innej
/// aplikacji prawo budzenia naszej.
/// </remarks>
[BroadcastReceiver(Enabled = true, Exported = false)]
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
                // Powiadomienia podpinane **przed** składaniem zależności, bo proces
                // obudzony budzikiem nie ma okna — a samo składanie nadrabia zaległe
                // przypomnienia. Podpięte po nim znaczyło, że to, po co budzik przyszedł,
                // zostawało zapisane jako pokazane i nie pokazywało się nigdzie.
                Powiadomienia.Podepnij(kontekst);

                await AppServices.ReadyAsync();

                if (intent?.Action == Budzik.Akcja)
                {
                    var ile = await AppServices.Provider
                        .GetRequiredService<ReminderService>()
                        .RunAsync();

                    await Budzik.Zapisz(
                        "Przypomnienia: budzik odebrany",
                        ile == 0 ? "nie było czego pokazać" : $"pokazane: {ile}");
                }

                // Następny budzik liczony na końcu: musi znać bazę, bo najbliższa chwila
                // bierze się z zadań. Zaglądaniem na Dysk zajmuje się osobno
                // SynchronizacjaWorker — budzik nie jest narzędziem do pracy okresowej.
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

    /// <summary>Wołane także po starcie telefonu — patrz OdbiorcaStartu.</summary>
    internal static void Obudz(Context kontekst)
    {
        _ = Budzik.PrzestawAsync(kontekst);
        SynchronizacjaWorker.Nastaw(kontekst);
    }
}

/// <summary>
/// Start telefonu: nastawienie budzików od nowa.
/// </summary>
/// <remarks>
/// Budziki nie przeżywają wyłączenia. Bez tego przypomnienia milkną po każdym
/// restarcie aż do następnego otwarcia aplikacji — i nie widać, że zamilkły,
/// bo cisza wygląda tak samo jak brak przypomnień.
///
/// Wystawiony, bo to system go woła, a do niewystawionego nie ma jak dotrzeć.
/// Ta klasa nie robi nic poza nastawieniem budzików, więc nie ma tu czego nadużyć.
/// </remarks>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted])]
internal sealed class OdbiorcaStartu : BroadcastReceiver
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
                // Jak wyżej: składanie nadrabia zaległe przypomnienia, więc haczyk
                // musi już być. Po starcie telefonu zaległych bywa najwięcej.
                Powiadomienia.Podepnij(kontekst);

                await AppServices.ReadyAsync();
                await Budzik.Zapisz("Przypomnienia: start telefonu", "budziki nastawione od nowa");
                OdbiorcaBudzika.Obudz(kontekst);
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
