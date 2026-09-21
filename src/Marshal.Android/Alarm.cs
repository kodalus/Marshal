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
internal static class Alarm
{
    public const string Action = "com.kodalus.marshal.PRZYPOMNIENIE";

    /// <summary>Budzik na północ. Osobny, bo odpowiada na inne pytanie niż przypomnienia.</summary>
    public const string MidnightAction = "com.kodalus.marshal.POLNOC";

    private const int Number = 7101;

    private const int MidnightNumber = 7102;

    /// <summary>Przestawienie budzika na najbliższą chwilę. Wołane po każdej zmianie.</summary>
    public static async Task RescheduleAsync(Context context)
    {
        try
        {
            await AppServices.ReadyAsync();

            var nearest = await AppServices.Provider
                .GetRequiredService<ReminderService>()
                .NextUpAsync();

            if (context.GetSystemService(Context.AlarmService) is not AlarmManager clock)
            {
                return;
            }

            var intent = Intent(context);

            var journal = AppServices.Provider.GetRequiredService<IActivityLog>();

            if (nearest is not { } moment)
            {
                // Nic nie czeka — budzik skasowany, żeby system nie budził nas po nic.
                clock.Cancel(intent);
                await journal.RecordAsync("Przypomnienia: budzik", "nic nie czeka");
                return;
            }

            var when = moment.ToUnixTimeMilliseconds();

            // „AllowWhileIdle", bo bez tego drzemka systemu przesuwa przypomnienia
            // ustawione na noc na rano — czyli dokładnie wtedy, gdy przestają być
            // potrzebne.
            var exact = Exact(clock);

            if (exact)
            {
                clock.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, when, intent);
            }
            else
            {
                clock.SetAndAllowWhileIdle(AlarmType.RtcWakeup, when, intent);
            }

            // Ślad w dzienniku, bo przy zamkniętej aplikacji nie ma **żadnego** innego
            // sposobu, żeby odróżnić trzy rzeczy wyglądające tak samo: budzik nienastawiony,
            // nastawiony i niedostarczony przez system, dostarczony i bez czego pokazać.
            await journal.RecordAsync(
                "Przypomnienia: budzik",
                $"nastawiony na {moment:yyyy-MM-dd HH:mm zzz}"
                    + (exact ? string.Empty : " (niedokładny — system nie dał zgody)"));
        }
        catch (Exception e)
        {
            // Bez budzika przypomnienia działają po staremu, czyli przy otwartym oknie.
            InAppNotifier.SystemStatus = $"budzik: {e.GetType().Name}: {e.Message}";
            await Save("Przypomnienia: budzik", "nie udało się nastawić", e);
        }
    }

    /// <summary>Wpis do dziennika, który nie wywraca wołającego, gdy baza nie stoi.</summary>
    public static async Task Save(
        string what, string content, Exception? error = null, ActivityLevel? level = null)
    {
        try
        {
            await AppServices.Provider.GetRequiredService<IActivityLog>().RecordAsync(
                what, content,
                level ?? (error is null ? ActivityLevel.Ok : ActivityLevel.Problem),
                error?.ToString());
        }
        catch
        {
            // Dziennik jest tu narzędziem do patrzenia, a nie częścią działania.
        }
    }

    private static bool Exact(AlarmManager clock) =>
        !OperatingSystem.IsAndroidVersionAtLeast(31) || clock.CanScheduleExactAlarms();

    /// <summary>
    /// Budzik na najbliższą północ — po to, żeby kafelek przestawił datę.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kafelek liczy dzisiejszy dzień przy rysowaniu i nie rysuje się sam z siebie.
    /// Bez tego budzika stał z wczorajszą datą i wczorajszym planem aż do pierwszego
    /// zdarzenia, które go obudzi — czyli do rana, gdy ktoś otworzy aplikację. Ekran
    /// domowy pokazywał więc wczoraj, i to dokładnie wtedy, gdy się na niego patrzy
    /// po przebudzeniu.
    /// </para>
    /// <para>
    /// Odstęp policzony przez strefę, a nie przez dodanie doby. Noc zmiany czasu ma
    /// dwadzieścia trzy albo dwadzieścia pięć godzin, więc doba dodana na ślepo mija
    /// się z północą o godzinę — raz za wcześnie, a to gorsze: kafelek przestawiłby
    /// datę na jutro, będąc jeszcze dziś.
    /// </para>
    /// <para>
    /// Dziesięć sekund po północy, nie w nią. Budzik potrafi odezwać się chwilę przed
    /// zadaną chwilą, a rysowanie przed północą liczy „dzisiaj" wciąż po staremu —
    /// czyli przerysowuje kafelek na to samo i data zostaje wczorajsza aż do rana.
    /// </para>
    /// </remarks>
    public static void ScheduleMidnight(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.AlarmService) is not AlarmManager clock)
            {
                return;
            }

            var local = DateTime.Today.AddDays(1).AddSeconds(10);
            var when = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local))
                .ToUnixTimeMilliseconds();

            var intent = PendingIntent.GetBroadcast(
                context,
                MidnightNumber,
                new Intent(context, typeof(AlarmReceiver)).SetAction(MidnightAction),
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

            if (Exact(clock))
            {
                clock.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, when, intent);
            }
            else
            {
                clock.SetAndAllowWhileIdle(AlarmType.RtcWakeup, when, intent);
            }
        }
        catch (Exception e)
        {
            // Bez tego budzika kafelek przestawia datę przy najbliższej okazji,
            // a nie o północy. Gorzej, ale nie na tyle, żeby coś wywracać.
            global::Android.Util.Log.Warn("Marshal", e.ToString());
        }
    }

    private static PendingIntent Intent(Context context)
    {
        var intent = new Intent(context, typeof(AlarmReceiver)).SetAction(Action);

        // „Immutable", bo nic w tym zamiarze nie ma być dopisywane z zewnątrz;
        // od Androida 12 jeden z tych dwóch znaczników jest zresztą wymagany.
        return PendingIntent.GetBroadcast(
            context, Number, intent,
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
internal sealed class AlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        var app = context.ApplicationContext ?? context;
        var waiting = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                // Powiadomienia podpinane **przed** składaniem zależności, bo proces
                // obudzony budzikiem nie ma okna — a samo składanie nadrabia zaległe
                // przypomnienia. Podpięte po nim znaczyło, że to, po co budzik przyszedł,
                // zostawało zapisane jako pokazane i nie pokazywało się nigdzie.
                Notifications.Hook(app);

                await AppServices.ReadyAsync();

                if (intent?.Action == Alarm.MidnightAction)
                {
                    // Sam dzień się zmienił: kafelek ma inną datę, inny plan i inny
                    // pasek tygodnia. Baza się nie zmieniła, więc nie ma czego liczyć
                    // poza przerysowaniem.
                    TodayWidget.Refresh(app);

                    await Alarm.Save("Kafelek: północ", "data przestawiona");
                }

                if (intent?.Action == Alarm.Action)
                {
                    var count = await AppServices.Provider
                        .GetRequiredService<ReminderService>()
                        .RunAsync();

                    await Alarm.Save(
                        "Przypomnienia: budzik odebrany",
                        count == 0 ? "nie było czego pokazać" : $"pokazane: {count}");
                }

                // Następny budzik liczony na końcu: musi znać bazę, bo najbliższa chwila
                // bierze się z zadań. Zaglądaniem na Dysk zajmuje się osobno
                // SynchronizacjaWorker — budzik nie jest narzędziem do pracy okresowej.
                await Alarm.RescheduleAsync(app);

                // Północ nastawiana po każdym budziku, nie tylko po sobie: to jedyne
                // miejsce, przez które ta aplikacja przechodzi bez okna, a budzik
                // nienastawiony nie zgłasza się sam.
                Alarm.ScheduleMidnight(app);
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
            finally
            {
                waiting?.Finish();
            }
        });
    }

    /// <summary>Wołane także po starcie telefonu — patrz OdbiorcaStartu.</summary>
    internal static void OnWake(Context context)
    {
        _ = Alarm.RescheduleAsync(context);
        Alarm.ScheduleMidnight(context);
        SyncWorker.Schedule(context);
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
internal sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        var app = context.ApplicationContext ?? context;
        var waiting = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                // Jak wyżej: składanie nadrabia zaległe przypomnienia, więc haczyk
                // musi już być. Po starcie telefonu zaległych bywa najwięcej.
                Notifications.Hook(app);

                await AppServices.ReadyAsync();
                await Alarm.Save("Przypomnienia: start telefonu", "budziki nastawione od nowa");
                AlarmReceiver.OnWake(app);
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
            finally
            {
                waiting?.Finish();
            }
        });
    }
}
