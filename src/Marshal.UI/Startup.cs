using System.Diagnostics;

namespace Marshal.UI;

/// <summary>
/// Znaczniki czasu rozruchu — do wpisu „Start" w dzienniku.
/// </summary>
/// <remarks>
/// <para>
/// Powstało z trzech nieudanych podejść do okienka „Marshal nie odpowiada". Za każdym
/// razem przenosiłem na wątek z puli to, co akurat podejrzewałem, i za każdym razem
/// okienko wracało — bo mierzone były wyłącznie te fazy, które już znałem. Liczby
/// z dziennika obalały kolejne hipotezy i żadna nie wskazywała następnej.
/// </para>
/// <para>
/// Te trzy zamykają lukę: czas od pierwszej linijki naszego kodu do postawienia okna,
/// reszta cyklu tworzenia i opóźnienie, z jakim wątek okna dochodzi do wczytywania.
/// Razem z tamtymi opisują cały rozruch, więc następna odpowiedź nie będzie już
/// zgadywaniem — albo któraś z sześciu liczb jest duża, albo blokada jest poza
/// aplikacją.
/// </para>
/// <para>
/// Zegar rusza przy pierwszym sięgnięciu do tej klasy, a robi to
/// <c>MainActivity.OnCreate</c> jako swoją pierwszą czynność. Wcześniej jest jeszcze
/// wystartowanie procesu i środowiska uruchomieniowego, czego z wnętrza aplikacji
/// zmierzyć się nie da — i to jest jedyna część rozruchu, o której te liczby nie mówią.
/// </para>
/// <para>
/// Na pulpicie nikt tu nie zagląda i wszystkie zostają zerami. Zero znaczy „nie ta
/// platforma", nie „zeszło zero czasu" — dlatego wpis pomija je w całości, zamiast
/// pisać same zera.
/// </para>
/// </remarks>
public static class Startup
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Ile minęło od pierwszej linijki naszego kodu.</summary>
    public static long Now() => Clock.ElapsedMilliseconds;

    /// <summary>Postawienie środowiska okna razem ze złożeniem widoku.</summary>
    public static long Platform { get; set; }

    /// <summary>Koniec tworzenia okna — z podpięciem powiadomień, cofania i budzika.</summary>
    public static long Window { get; set; }

    /// <summary>Chwila, w której wątek okna doszedł do wczytywania.</summary>
    public static long Model { get; set; }

    /// <summary>Czy jest o czym mówić. Na pulpicie nikt tych liczb nie ustawia.</summary>
    public static bool Measured => Window > 0;

    /// <summary>Najdłuższa przerwa w odpowiadaniu wątku okna i chwila, w której minęła.</summary>
    /// <remarks>
    /// <para>
    /// Liczby faz mówią, ile trwały <b>nazwane</b> kawałki rozruchu. Nie mówią nic
    /// o tym, czy między nimi wątek okna odpowiadał — a to jest dokładnie to, o co pyta
    /// Android, gdy pokazuje „aplikacja nie odpowiada". Trzy podejścia rozbiły się o tę
    /// różnicę: wszystkie fazy wychodziły średnie, a okienko wracało.
    /// </para>
    /// <para>
    /// Bicie serca mierzy to wprost i bez zgadywania, gdzie szukać: minutnik na wątku
    /// okna w tym samym miejscu kolejki, co obsługa dotknięć. Nie odpowiada na pytanie
    /// „co blokuje", ale odpowiada na „czy i kiedy" — a mając kiedy, co znajduje się
    /// już zwykłym czytaniem.
    /// </para>
    /// </remarks>
    public static long LongestGap { get; private set; }

    /// <summary>Kiedy skończyła się ta najdłuższa przerwa, licząc od startu.</summary>
    public static long GapPassed { get; private set; }

    private static long _lastBeat;

    /// <summary>Jedno uderzenie serca. Wołane z minutnika wątku okna.</summary>
    public static void Beat()
    {
        var now = Now();
        var gap = now - _lastBeat;

        if (_lastBeat > 0 && gap > LongestGap)
        {
            LongestGap = gap;
            GapPassed = now;
        }

        _lastBeat = now;
    }

    public static string Description =>
        $"postawienie {Platform} ms, reszta okna {Window - Platform} ms, "
        + $"do wczytywania {Model - Window} ms, "
        + $"najdłuższa przerwa {LongestGap} ms (minęła w {GapPassed} ms)";
}
