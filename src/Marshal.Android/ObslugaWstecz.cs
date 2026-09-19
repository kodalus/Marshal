using AndroidX.Activity;
using Marshal.UI;

namespace Marshal.Android;

/// <summary>
/// Odpowiedź na przycisk i gest wstecz.
/// </summary>
/// <remarks>
/// <para>
/// Przez <c>OnBackPressedDispatcher</c>, a nie przez przesłonięcie <c>OnBackPressed</c>.
/// To drugie wygląda prościej i **nie zadziałałoby**: przy aplikacji celującej w nowsze
/// wydania Androida system woła zapowiadane cofnięcie, a stara metoda przestaje być
/// wołana w ogóle. Awaria byłaby cicha — przycisk wstecz po prostu robiłby to, co
/// zawsze, i nikt by nie zgadł, że kod w ogóle istnieje.
/// </para>
/// <para>
/// Gdy okno nie ma nic do zamknięcia, cofnięcie oddawane jest systemowi: wyłączamy
/// się na moment i prosimy o cofnięcie jeszcze raz. Zamknięcie okna wprost byłoby
/// krótsze, ale odebrałoby to, co ma do powiedzenia biblioteka okienek — na przykład
/// zamknięcie otwartego menu podręcznego.
/// </para>
/// </remarks>
internal sealed class ObslugaWstecz(ComponentActivity window) : OnBackPressedCallback(true)
{
    public override void HandleOnBackPressed()
    {
        if (Back.Handled())
        {
            return;
        }

        Enabled = false;

        try
        {
            window.OnBackPressedDispatcher.OnBackPressed();
        }
        finally
        {
            Enabled = true;
        }
    }
}
