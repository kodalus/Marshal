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

    /// <summary>Co ile telefon ma sam zaglądać na Dysk.</summary>
    /// <remarks>
    /// Pół godziny to kompromis: częściej znaczy budzenie telefonu na okrągło, rzadziej
    /// znaczy, że zmiana z komputera czeka pół dnia. System i tak traktuje to jako
    /// „nie częściej niż" — budziki przepuszczane w uśpieniu mają własny limit,
    /// mniej więcej kwadransowy.
    /// </remarks>
    private static readonly long Odstep = AlarmManager.IntervalHalfHour;

    /// <summary>
    /// Nastawienie budzika na kolejny przebieg synchronizacji.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Jednorazowy i budzący, nie powtarzalny.</b> Do dziś stało tu
    /// <c>SetInexactRepeating</c> z <c>AlarmType.Rtc</c> i to były dwie wady naraz.
    /// <c>Rtc</c> nie budzi telefonu — czeka, aż ten obudzi się sam z innego powodu.
    /// A budzik powtarzalny jest w uśpieniu systemu odkładany bez ograniczenia:
    /// telefon leżący w kieszeni potrafi nie odpalić go ani razu.
    /// </para>
    /// <para>
    /// Skutek widać było dokładnie tam, gdzie trzeba: zadanie zmienione na komputerze
    /// nie dawało na telefonie żadnego znaku, dopóki aplikacji się nie otworzyło —
    /// a po otwarciu i zamknięciu przypomnienie przychodziło normalnie. Czyli budzik
    /// przypomnień działał, a nie działało <b>dowiadywanie się</b> o przypomnieniu.
    /// </para>
    /// <para>
    /// Teraz tak samo jak przy przypomnieniach: <c>RtcWakeup</c> i „wolno także
    /// w uśpieniu". Jednorazowy, bo tylko takiemu system pozwala przejść przez
    /// uśpienie; następny nastawia się po każdym odebraniu — a że odbiornik nastawia
    /// budziki po <b>każdym</b> przebiegu, łańcuch domyka się sam. Gdyby się urwał,
    /// podnosi go otwarcie aplikacji, jej zamknięcie i start telefonu.
    /// </para>
    /// <para>
    /// Do pracy okresowej służy zwykle WorkManager i to jest następny krok, jeśli to
    /// nie wystarczy: budzik przepuszczony w uśpieniu dostaje około dziesięciu sekund
    /// na pracę razem z siecią, a pełny przebieg do Dysku bywa dłuższy. WorkManager
    /// daje na to dziesięć minut i sam ponawia — kosztem kolejnej biblioteki i kodu
    /// pisanego na ślepo.
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

            zegar.SetAndAllowWhileIdle(
                AlarmType.RtcWakeup,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Odstep,
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

                // Następny budzik synchronizacji też **przed** pracą, nie po niej.
                // Budzik jest jednorazowy i sam nastawia następny, więc cały łańcuch
                // wisi na tym, że to wywołanie się wykona. Praca przed nim — składanie
                // zależności, migracje, przebieg do Dysku — bywa dłuższa niż czas, który
                // system daje obudzonemu odbiornikowi; ubity w połowie odbiornik urwałby
                // łańcuch na zawsze, a wyglądałoby to jak cisza bez przyczyny.
                // Nie potrzebuje bazy ani zależności: dotyka wyłącznie budzika systemu.
                Budzik.NastawSynchronizacje(kontekst);

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

                // Budzik przypomnień na końcu, bo ten musi znać bazę: liczy najbliższą
                // chwilę z zadań. Synchronizacja została nastawiona na początku.
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
            // Ze śladem, bo to jedyna droga, na której budzik odpala się poprawnie
            // i nie robi nic. Bez wpisu wygląda identycznie jak budzik, który nie
            // przyszedł — a to dwie różne rzeczy do naprawienia.
            await Budzik.Zapisz(
                "Synchronizacja w tle", "brak poświadczeń albo żetonu", poziom: ActivityLevel.Problem);

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
