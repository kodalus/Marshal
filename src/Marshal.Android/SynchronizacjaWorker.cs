using Android.Content;
using AndroidX.Work;
using Java.Util.Concurrent;
using Marshal.Application.UseCases;
using Marshal.Domain.Diagnostics;
using Marshal.Infrastructure.Sync.Google;
using Marshal.UI;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.Android;

/// <summary>
/// Zaglądanie na Dysk w tle, co pół godziny.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dlaczego nie budzik.</b> Budzik systemowy jest narzędziem do „zrób coś o tej
/// godzinie". Praca okresowa zrobiona z budzików to trzy osobne rzeczy do dopilnowania,
/// i każda z nich zawiodła po kolei: budzik niebudzący nie wybudzał telefonu, budzik
/// powtarzalny był w uśpieniu odkładany bez ograniczenia, a łańcuch jednorazowych
/// urywał się, gdy system ubił odbiornik w połowie pracy. Objaw za każdym razem był
/// ten sam i żaden nie wskazywał na przyczynę: cisza.
/// </para>
/// <para>
/// WorkManager jest dokładnie tym, czym te trzy próby chciały być. Przeżywa uśpienie
/// i restart telefonu, sam ponawia nieudany przebieg, sam pilnuje, żeby nie ruszać bez
/// sieci — a przede wszystkim daje na pracę <b>dziesięć minut</b> zamiast dziesięciu
/// sekund, które dostaje odbiornik obudzony budzikiem. Pełny przebieg do Dysku bywa
/// dłuższy od dziesięciu sekund i to była ostatnia rzecz, która i tak by go dobiła.
/// </para>
/// <para>
/// Czego to nie zmienia: <b>przypomnienia zostają przy budziku</b>. Tam pytanie brzmi
/// „odezwij się o szesnastej", a nie „zajrzyj kiedyś w ciągu najbliższego kwadransa",
/// i budzik jest na to jedyną odpowiedzią. Ten worker po każdym przebiegu przestawia
/// budzik przypomnień, bo to, co przyszło z Dysku, potrafi zmienić najbliższą godzinę.
/// </para>
/// <para>
/// Bez powiadomienia na pasku: praca okresowa z warunkiem sieci nie jest usługą
/// pierwszoplanową i nie ma czego pokazywać. Widać ją w dzienniku, gdy coś przeniosła.
/// </para>
/// </remarks>
public sealed class SynchronizacjaWorker : Worker
{
    /// <summary>
    /// Nazwa zlecenia. Jedna, bo zlecenie ma być jedno.
    /// </summary>
    /// <remarks>
    /// Po niej system rozpoznaje, że to wciąż to samo zlecenie, i nie zakłada drugiego
    /// przy każdym otwarciu aplikacji.
    /// </remarks>
    private const string Name = "marshal-synchronizacja";

    /// <summary>Co ile zaglądać. Kwadrans to dolna granica narzucona przez system.</summary>
    private const long Minut = 30;

    public SynchronizacjaWorker(Context kontekst, WorkerParameters parametry)
        : base(kontekst, parametry)
    {
    }

    /// <summary>
    /// Zgłoszenie zlecenia. Wołane przy otwarciu aplikacji, przy wyjściu i po starcie telefonu.
    /// </summary>
    /// <remarks>
    /// <b>„Zostaw, jeśli już jest"</b>, a nie „zastąp". Zastąpienie przestawiałoby odliczanie
    /// na nowo przy każdym otwarciu aplikacji — a przy zaglądaniu do niej co kwadrans
    /// przebieg w tle nie ruszyłby ani razu. Wołanie tego wielokrotnie jest wtedy
    /// bezpieczne i o to chodzi: nie ma jednego miejsca, o które trzeba by zadbać.
    /// </remarks>
    public static void Nastaw(Context kontekst)
    {
        try
        {
            var conditions = new Constraints.Builder()
                .SetRequiredNetworkType(NetworkType.Connected!)!
                .Build();

            // Budowniczy trzymany w zmiennej, a nie łączony w łańcuch: w Javie te metody
            // oddają typ nadrzędny i łańcuch kończyłby się zleceniem bez okresu.
            var budowniczy = new PeriodicWorkRequest.Builder(
                Java.Lang.Class.FromType(typeof(SynchronizacjaWorker)),
                Minut,
                TimeUnit.Minutes!);

            budowniczy.SetConstraints(conditions!);

            WorkManager.GetInstance(kontekst).EnqueueUniquePeriodicWork(
                Name,
                ExistingPeriodicWorkPolicy.Keep!,
                (PeriodicWorkRequest)budowniczy.Build());
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Marshal", e.ToString());
        }
    }

    /// <summary>
    /// Jeden przebieg.
    /// </summary>
    /// <remarks>
    /// Czekanie na zadanie asynchroniczne jest tu w porządku: <c>DoWork</c> woła się na
    /// wątku roboczym, na którym wolno stać, i ma stać aż do końca pracy. Wątek nie ma
    /// kontekstu synchronizacji, więc nie ma się o co zakleszczyć.
    /// </remarks>
    public override Result DoWork()
    {
        try
        {
            // Haczyk na dymki przed składaniem zależności: składanie nadrabia zaległe
            // przypomnienia, a tu nie ma okna, które by je odebrało.
            Powiadomienia.Podepnij(ApplicationContext!);

            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Marshal", e.ToString());

            // Ponowienie, nie porażka: sieć, która odmówiła, zwykle odmawia chwilowo,
            // a system sam odsuwa kolejne próby coraz dalej.
            return Result.InvokeRetry()!;
        }
    }

    private async Task<Result> RunAsync()
    {
        await AppServices.ReadyAsync();

        var drive = AppServices.Provider.GetRequiredService<GoogleSyncService>();

        if (!drive.HasCredentials || !Directory.Exists(drive.TokenFolder))
        {
            // Ze śladem, bo to jedyna droga, na której przebieg odpala się poprawnie
            // i nie robi nic. Bez wpisu wygląda identycznie jak przebieg, który nie
            // ruszył — a to dwie różne rzeczy do naprawienia.
            await Budzik.Save(
                "Synchronizacja w tle", "brak poświadczeń albo żetonu",
                level: ActivityLevel.Problem);

            return Result.InvokeSuccess()!;
        }

        var result = await drive.SyncAsync();

        if (!result.Ok)
        {
            await Budzik.Save(
                "Synchronizacja w tle", result.Message, level: ActivityLevel.Problem);

            return Result.InvokeRetry()!;
        }

        if (result.Applied > 0)
        {
            // Przypomnienia od razu, nie przy następnym budziku: to, co właśnie przyszło,
            // bywa już zaległe. Zadanie zmienione rano na komputerze dociera tu po
            // południu i ma się odezwać teraz.
            var count = await AppServices.Provider
                .GetRequiredService<ReminderService>()
                .RunAsync();

            await Budzik.Save(
                "Synchronizacja w tle",
                $"przyjęte {result.Applied}"
                    + (count > 0 ? $", przypomnienia pokazane: {count}" : string.Empty));

            TodayWidget.Refresh(ApplicationContext!);
        }

        // Budzik przypomnień przestawiany po każdym przebiegu, także pustym: najbliższa
        // godzina mogła się zmienić, a przy zamkniętej aplikacji nie ma kto jej sprawdzić.
        await Budzik.PrzestawAsync(ApplicationContext!);

        return Result.InvokeSuccess()!;
    }
}
