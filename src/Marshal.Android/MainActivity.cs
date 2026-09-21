using System.Globalization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using Marshal.Infrastructure.Sync.Google;
using Marshal.UI;

namespace Marshal.Android;

[Activity(
    Label = "Marshal",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<App>
{
    /// <summary>Prośba z widgetu, żeby wejść od razu na kalendarz.</summary>
    public const string CalendarExtra = "kalendarz";

    /// <summary>Zadanie do otwarcia razem z kalendarzem. Puste, gdy dotknięto samego kafelka.</summary>
    public const string TaskExtra = "zadanie-kalendarza";

    /// <summary>
    /// Wydarzenie z podłączonego kalendarza: skąd pochodzi, czym jest u źródła
    /// i pod którym dniem stało.
    /// </summary>
    /// <remarks>
    /// Trzy wartości zamiast jednej, bo wydarzenie nie ma u nas identyfikatora —
    /// istnieje w cudzym kalendarzu, a my mamy jego kopię. Dzień dokładany dlatego,
    /// że kopia sama nie mówi, którego dnia szukać jej na siatce, a kafelek pokazuje
    /// dowolny dzień.
    /// </remarks>
    public const string SourceExtra = "kalendarz-wydarzenia";

    public const string EventExtra = "wydarzenie-kalendarza";

    public const string DayExtra = "dzien-wydarzenia";

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();

    /// <summary>
    /// Pilnowanie awarii, ślad po poprzedniej i powiadomienia systemowe.
    /// </summary>
    /// <remarks>
    /// Powiadomienia po <c>base.OnCreate</c>, bo dopiero ono stawia Avalonię
    /// i aplikację — a haczyk na powiadomienia siedzi w warstwie współdzielonej,
    /// która wtedy dopiero istnieje.
    /// </remarks>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Zegar rozruchu jako pierwsza czynność — zob. Rozruch. Wcześniej jest już
        // tylko start procesu i środowiska, czego stąd zmierzyć się nie da.
        _ = Startup.Now();

        // Przed wszystkim: okno składa się już z tą wiedzą, a od niej zależy, czy
        // w ogóle rysować rzeczy pomyślane pod kursor. Zob. Platforma.
        Platform.Touch = true;

        // Przed bazowym, bo to ono stawia Avalonię — a awaria przy stawianiu jest
        // dokładnie tą, o której najtrudniej się czegokolwiek dowiedzieć.
        Crash.Watch(this);
        Crash.Read(this);

        // **Przed bazowym**, bo to ono stawia Avalonię, a Avalonia od razu składa okno
        // razem z polami daty i godziny. Pole pyta przy powstawaniu, czy jest czym
        // pokazać okienko systemu; podpięte linijkę później znaczyło, że odpowiedź
        // zawsze brzmiała „nie" i wszystkie pola zostawały przy wybieraku wbudowanym.
        // Samo podpięcie niczego nie otwiera, więc nie potrzebuje gotowego okna.
        NativePickers.Hook(this);

        // Bazowe stawia Avalonię i składa cały widok — to jest ta część rozruchu,
        // której dotąd nie mierzyłem, a która idzie wątkiem okna w całości.
        base.OnCreate(savedInstanceState);
        Startup.Platform = Startup.Now();

        Notifications.Hook(this);

        // Po bazowym, bo dopiero ono stawia okno — a nasza odpowiedź na cofnięcie
        // pyta o to, co w tym oknie jest otwarte.
        OnBackPressedDispatcher.AddCallback(this, new BackHandler(this));

        HandleIntent(Intent);

        // Droga po zgodę Google. Kontekst aplikacji, nie okna: zgoda przeżywa obrót
        // telefonu i zamknięcie okna, a okno zapamiętane w polu statycznym zostałoby
        // w pamięci na długo po tym, jak przestało istnieć.
        GoogleDriveFactory.CodeReceiver = () => new CodeReceiverAndroid(ApplicationContext!);

        // Budziki nastawiane także przy otwieraniu, nie tylko przy wychodzeniu.
        // Wyjście bywa gwałtowne — zdjęcie aplikacji z listy ostatnich potrafi zabić
        // proces, zanim nastawianie dobiegnie końca — a wtedy budzik nie istnieje
        // i nie widać tego po niczym. Otwarcie jest chwilą, w której da się to nadrobić.
        AlarmReceiver.OnWake(ApplicationContext!);

        Startup.Window = Startup.Now();
    }

    /// <summary>
    /// Odświeżenie widgetu przy wyjściu z aplikacji.
    /// </summary>
    /// <remarks>
    /// Widget nie ma jak dowiedzieć się o zmianie sam: system odpytuje go rzadko,
    /// a częściej nie pozwoli. Wyjście z aplikacji jest jedyną chwilą, o której
    /// wiadomo na pewno, że coś mogło się zmienić i że za moment będzie widać
    /// ekran domowy.
    /// </remarks>
    /// <summary>
    /// Ponowne wejście do okna, które już stoi.
    /// </summary>
    /// <remarks>
    /// Aplikacja otwiera się w trybie „jedno na wierzchu", więc dotknięcie widgetu przy
    /// działającym oknie <b>nie</b> woła OnCreate — nowy zamiar przychodzi tędy. Bez tego
    /// prośba o kalendarz działałaby wyłącznie przy zamkniętej aplikacji, co jest
    /// najtrudniejszym do zauważenia rodzajem połowicznego działania.
    /// </remarks>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        // Zapamiętany, bo system nie podmienia go sam: bez tego kolejne odczyty
        // widziałyby wciąż zamiar, którym okno zostało otwarte za pierwszym razem.
        Intent = intent;
        HandleIntent(intent);
    }

    private static void HandleIntent(Intent? intent)
    {
        if (intent?.GetBooleanExtra(CalendarExtra, false) != true)
        {
            return;
        }

        // Nieczytelny identyfikator traktowany jak jego brak: wejście na kalendarz jest
        // wtedy nadal sensowną odpowiedzią, a odmowa całego wejścia — nie. Tak samo
        // niepełny opis wydarzenia: bez któregokolwiek z trzech nie ma czego otwierać.
        if (Guid.TryParse(intent.GetStringExtra(TaskExtra), out var task))
        {
            App.AskForCalendar(new CalendarRequest(Task: task));
            return;
        }

        if (Guid.TryParse(intent.GetStringExtra(SourceExtra), out var source)
            && intent.GetStringExtra(EventExtra) is { Length: > 0 } external
            && DateOnly.TryParseExact(
                intent.GetStringExtra(DayExtra),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var day))
        {
            App.AskForCalendar(new CalendarRequest(Source: source, Event: external, Day: day));
            return;
        }

        // Sam dzień: wystąpienie rytmu z kafelka. Nie ma karty do otwarcia, jest dzień
        // do pokazania.
        if (DateOnly.TryParseExact(
            intent.GetStringExtra(DayExtra),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var alone))
        {
            App.AskForCalendar(new CalendarRequest(Day: alone));
            return;
        }

        App.AskForCalendar();
    }

    protected override void OnDestroy()
    {
        // Haczyk wskazujący na zamknięte okno jest gorszy od pustego: pusty znaczy
        // „użyj wbudowanego", a wskazujący na nic wywraca się dopiero przy dotknięciu.
        NativePickers.Unhook();

        base.OnDestroy();
    }

    /// <summary>
    /// Powrót na wierzch: minutnik okna rusza z powrotem i robi jeden przebieg od razu.
    /// </summary>
    /// <remarks>
    /// Tu, a nie na zdarzeniu okna Avalonii: zejście w tło i powrót to pojęcia Androida
    /// i tylko Android wie o nich na pewno. Zob. <see cref="Sleep"/>.
    /// </remarks>
    protected override void OnResume()
    {
        base.OnResume();
        Sleep.Leave();
    }

    protected override void OnPause()
    {
        base.OnPause();

        // Najpierw uśpienie minutnika, potem reszta. Praca w tle należy do pracownika
        // synchronizacji i budzika — mechanizmów, którym system na nią pozwala — a nie
        // do minutnika okna, którego nikt już nie ogląda.
        Sleep.Enter();

        TodayWidget.Refresh(this);

        // Budzik nastawiany przy wychodzeniu z aplikacji, bo to jedyna chwila, o której
        // wiadomo na pewno, że lista przypomnień jest już taka, jaka ma być — i zaraz
        // przestanie być komu jej pilnować.
        _ = Alarm.RescheduleAsync(ApplicationContext!);
        SyncWorker.Schedule(ApplicationContext!);
    }
}
