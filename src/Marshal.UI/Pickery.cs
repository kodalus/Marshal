namespace Marshal.UI;

/// <summary>
/// Wybieranie daty i godziny okienkiem systemu, gdy platforma je ma.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia rysuje własne wybieraki — trzy kolumny do przewijania — i na myszy są one
/// w porządku, bo da się w nie po prostu wpisać. Na telefonie nie: tam palec zna
/// okrągłą tarczę zegara i kalendarz miesiąca z każdej innej aplikacji, a trzy kolumny
/// do kręcenia są tym, czego trzeba się nauczyć od nowa w jednej jedynej aplikacji.
/// </para>
/// <para>
/// Tej tarczy nie da się w Avalonii włączyć, bo jest oknem systemu, a nie kontrolką.
/// Stąd haczyk: warstwa wspólna go woła, projekt Androida podstawia, a pulpit zostawia
/// pusty i dostaje wybierak wbudowany. Ten sam wzór, co przy powiadomieniach systemowych
/// i przy odbieraniu zgody Google — okno nie wie, kto pod nim stoi.
/// </para>
/// <para>
/// Pusty haczyk nie jest brakiem: znaczy „ta platforma nie ma czego pokazać", a wtedy
/// wybierak wbudowany jest poprawną odpowiedzią, nie zastępczą.
/// </para>
/// </remarks>
public static class Pickery
{
    /// <summary>Oddaje wybraną datę albo tę podaną, gdy okno zamknięto bez wyboru.</summary>
    public static Func<DateOnly?, Task<DateOnly?>>? Data { get; set; }

    /// <summary>Oddaje wybraną godzinę albo tę podaną, gdy okno zamknięto bez wyboru.</summary>
    public static Func<TimeOnly?, Task<TimeOnly?>>? Hour { get; set; }

    /// <summary>Czy platforma ma własne okienka. Rozstrzyga, który wybierak jest widoczny.</summary>
    public static bool SystemSink => Data is not null && Hour is not null;
}
