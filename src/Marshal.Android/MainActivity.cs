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
    public const string KalendarzExtra = "kalendarz";

    /// <summary>Zadanie do otwarcia razem z kalendarzem. Puste, gdy dotknięto samego kafelka.</summary>
    public const string ZadanieExtra = "zadanie-kalendarza";

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
        // Przed bazowym, bo to ono stawia Avalonię — a awaria przy stawianiu jest
        // dokładnie tą, o której najtrudniej się czegokolwiek dowiedzieć.
        Awaria.Pilnuj(this);
        Awaria.Odczytaj(this);

        base.OnCreate(savedInstanceState);
        Powiadomienia.Podepnij(this);

        Rozpatrz(Intent);

        // Droga po zgodę Google. Kontekst aplikacji, nie okna: zgoda przeżywa obrót
        // telefonu i zamknięcie okna, a okno zapamiętane w polu statycznym zostałoby
        // w pamięci na długo po tym, jak przestało istnieć.
        GoogleDriveFactory.OdbiorcaKodu = () => new OdbiorcaKoduAndroid(ApplicationContext!);

        // Budziki nastawiane także przy otwieraniu, nie tylko przy wychodzeniu.
        // Wyjście bywa gwałtowne — zdjęcie aplikacji z listy ostatnich potrafi zabić
        // proces, zanim nastawianie dobiegnie końca — a wtedy budzik nie istnieje
        // i nie widać tego po niczym. Otwarcie jest chwilą, w której da się to nadrobić.
        OdbiorcaBudzika.Obudz(ApplicationContext!);
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
        Rozpatrz(intent);
    }

    private static void Rozpatrz(Intent? zamiar)
    {
        if (zamiar?.GetBooleanExtra(KalendarzExtra, false) != true)
        {
            return;
        }

        // Nieczytelny identyfikator traktowany jak jego brak: wejście na kalendarz jest
        // wtedy nadal sensowną odpowiedzią, a odmowa całego wejścia — nie.
        App.PoprosOKalendarz(
            Guid.TryParse(zamiar.GetStringExtra(ZadanieExtra), out var zadanie)
                ? zadanie
                : null);
    }

    protected override void OnPause()
    {
        base.OnPause();
        TodayWidget.Refresh(this);

        // Budzik nastawiany przy wychodzeniu z aplikacji, bo to jedyna chwila, o której
        // wiadomo na pewno, że lista przypomnień jest już taka, jaka ma być — i zaraz
        // przestanie być komu jej pilnować.
        _ = Budzik.PrzestawAsync(ApplicationContext!);
        SynchronizacjaWorker.Nastaw(ApplicationContext!);
    }
}
