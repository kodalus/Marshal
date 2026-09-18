using Android.App;
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

        // Droga po zgodę Google. Kontekst aplikacji, nie okna: zgoda przeżywa obrót
        // telefonu i zamknięcie okna, a okno zapamiętane w polu statycznym zostałoby
        // w pamięci na długo po tym, jak przestało istnieć.
        GoogleDriveFactory.OdbiorcaKodu = () => new OdbiorcaKoduAndroid(ApplicationContext!);
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
    protected override void OnPause()
    {
        base.OnPause();
        TodayWidget.Refresh(this);
    }
}
