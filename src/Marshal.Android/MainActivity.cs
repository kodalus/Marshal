using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
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
    /// Powiadomienia systemowe podpinane raz, przy zakładaniu okna.
    /// </summary>
    /// <remarks>
    /// Po <c>base.OnCreate</c>, bo dopiero ono stawia Avalonię i aplikację — a haczyk
    /// na powiadomienia siedzi w warstwie współdzielonej, która wtedy dopiero istnieje.
    /// </remarks>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Powiadomienia.Podepnij(this);
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
