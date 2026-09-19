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
public static class Rozruch
{
    private static readonly Stopwatch Zegar = Stopwatch.StartNew();

    /// <summary>Ile minęło od pierwszej linijki naszego kodu.</summary>
    public static long Teraz() => Zegar.ElapsedMilliseconds;

    /// <summary>Postawienie środowiska okna razem ze złożeniem widoku.</summary>
    public static long Platforma { get; set; }

    /// <summary>Koniec tworzenia okna — z podpięciem powiadomień, cofania i budzika.</summary>
    public static long Okno { get; set; }

    /// <summary>Chwila, w której wątek okna doszedł do wczytywania.</summary>
    public static long Model { get; set; }

    /// <summary>Czy jest o czym mówić. Na pulpicie nikt tych liczb nie ustawia.</summary>
    public static bool Zmierzony => Okno > 0;

    public static string Opis =>
        $"postawienie {Platforma} ms, reszta okna {Okno - Platforma} ms, "
        + $"do wczytywania {Model - Okno} ms";
}
