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
    public static long NajdluzszaPrzerwa { get; private set; }

    /// <summary>Kiedy skończyła się ta najdłuższa przerwa, licząc od startu.</summary>
    public static long PrzerwaMinela { get; private set; }

    private static long _ostatnieBicie;

    /// <summary>Jedno uderzenie serca. Wołane z minutnika wątku okna.</summary>
    public static void Bicie()
    {
        var teraz = Teraz();
        var przerwa = teraz - _ostatnieBicie;

        if (_ostatnieBicie > 0 && przerwa > NajdluzszaPrzerwa)
        {
            NajdluzszaPrzerwa = przerwa;
            PrzerwaMinela = teraz;
        }

        _ostatnieBicie = teraz;
    }

    public static string Opis =>
        $"postawienie {Platforma} ms, reszta okna {Okno - Platforma} ms, "
        + $"do wczytywania {Model - Okno} ms, "
        + $"najdłuższa przerwa {NajdluzszaPrzerwa} ms (minęła w {PrzerwaMinela} ms)";
}
