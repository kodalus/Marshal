using Android.App;
using Android.Content;
using Android.OS;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Diagnostics;
using Marshal.Infrastructure.Notifications;
using Marshal.Infrastructure.Sync.Google;
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

    public const string AkcjaSynchronizacji = "com.kodalus.marshal.SYNCHRONIZACJA";

    private const int Numer = 7101;

    private const int NumerSynchronizacji = 7102;

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

    /// <summary>
    /// Nastawienie powtarzalnego budzika na synchronizację.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Niedokładny i powtarzalny: system łączy takie budziki w paczki i odpala je,
    /// kiedy i tak kogoś budzi. „Co pół godziny" znaczy więc „nie częściej niż",
    /// i dobrze — synchronizacja co do minuty nie jest nic warta, a wybudzanie
    /// telefonu co pół godziny na okrągło kosztuje baterię.
    /// </para>
    /// <para>
    /// Mechanizm ten sam co przy przypomnieniach, choć do pracy okresowej służy
    /// zwykle WorkManager. Powód jest prosty: WorkManager to kolejna biblioteka
    /// i kolejny rodzaj kodu do napisania na ślepo, a różnica sprowadza się do
    /// warunku sieci, który tutaj zastępuje nieudany przebieg i wpis w dzienniku.
    /// Do przepisania wtedy, gdy okaże się, że budzik jest za rzadki albo za drogi.
    /// </para>
    /// </remarks>
    public static void NastawSynchronizacje(Context kontekst)
    {
        try
        {
            if (kontekst.GetSystemService(Context.AlarmService) is not AlarmManager zegar)
            {
                return;
            }

            zegar.SetInexactRepeating(
                AlarmType.Rtc,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + AlarmManager.IntervalHalfHour,
                AlarmManager.IntervalHalfHour,
                Zamiar(kontekst, AkcjaSynchronizacji, NumerSynchronizacji));
        }
        catch (Exception e)
        {
            InAppNotifier.StanSystemowych = $"budzik synchronizacji: {e.GetType().Name}: {e.Message}";
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

    private static PendingIntent Zamiar(Context kontekst) =>
        Zamiar(kontekst, Akcja, Numer);

    private static PendingIntent Zamiar(Context kontekst, string akcja, int numer)
    {
        var zamiar = new Intent(kontekst, typeof(OdbiorcaBudzika)).SetAction(akcja);

        // „Immutable", bo nic w tym zamiarze nie ma być dopisywane z zewnątrz;
        // od Androida 12 jeden z tych dwóch znaczników jest zresztą wymagany.
        return PendingIntent.GetBroadcast(
            kontekst, numer, zamiar,
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

                // Bez wyjścia po synchronizacji. Do dziś budzik synchronizacji kończył
                // się tutaj, **przed** przestawieniem budzika przypomnień — a to znaczyło,
                // że przypomnienie przyniesione właśnie przez synchronizację nie miało
                // się od czego odezwać. Zadanie zmienione na komputerze docierało na
                // telefon i milczało do najbliższego otwarcia aplikacji.
                if (intent?.Action == Budzik.AkcjaSynchronizacji)
                {
                    await SynchronizujAsync();
                }
                else if (intent?.Action == Budzik.Akcja)
                {
                    var ile = await AppServices.Provider
                        .GetRequiredService<ReminderService>()
                        .RunAsync();

                    await Budzik.Zapisz(
                        "Przypomnienia: budzik odebrany",
                        ile == 0 ? "nie było czego pokazać" : $"pokazane: {ile}");
                }

                await Budzik.PrzestawAsync(kontekst);
                Budzik.NastawSynchronizacje(kontekst);
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

    /// <summary>
    /// Przebieg synchronizacji w tle.
    /// </summary>
    /// <remarks>
    /// Tylko wtedy, gdy żeton już jest. Bez niego logowanie chciałoby otworzyć
    /// przeglądarkę — z tła, gdzie system i tak na to nie pozwoli, a gdyby pozwolił,
    /// byłoby to okno wyskakujące bez powodu w środku czegoś innego.
    /// </remarks>
    /// <summary>Wołane także po starcie telefonu — patrz OdbiorcaStartu.</summary>
    internal static void Obudz(Context kontekst)
    {
        _ = Budzik.PrzestawAsync(kontekst);
        Budzik.NastawSynchronizacje(kontekst);
    }

    private static async Task SynchronizujAsync()
    {
        var dysk = AppServices.Provider.GetRequiredService<GoogleSyncService>();

        if (!dysk.HasCredentials || !Directory.Exists(dysk.TokenFolder))
        {
            return;
        }

        var wynik = await dysk.SyncAsync();

        if (!wynik.Ok)
        {
            await Budzik.Zapisz("Synchronizacja w tle", wynik.Message, poziom: ActivityLevel.Problem);
            return;
        }

        if (wynik.Applied == 0)
        {
            // Cicho, gdy nic nie przyszło: budzik chodzi co pół godziny, a dziennik
            // trzyma pięćset wpisów.
            return;
        }

        // Przypomnienia **od razu**, nie dopiero przy następnym budziku. To, co właśnie
        // przyszło, bywa już zaległe: zadanie zmienione rano na komputerze dociera tu
        // po południu i ma się odezwać teraz, a nie za pół godziny. Przyszłymi zajmie
        // się przestawienie budzika, które idzie zaraz potem.
        var ile = await AppServices.Provider.GetRequiredService<ReminderService>().RunAsync();

        await Budzik.Zapisz(
            "Synchronizacja w tle",
            $"przyjęte {wynik.Applied}" + (ile > 0 ? $", przypomnienia pokazane: {ile}" : string.Empty));
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
