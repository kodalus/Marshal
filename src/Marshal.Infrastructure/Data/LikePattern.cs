namespace Marshal.Infrastructure.Data;

/// <summary>
/// Szukany tekst zamieniony na wzorzec „zawiera" — <b>ze znakami zapisu wyłączonymi</b>.
/// </summary>
/// <remarks>
/// <para>
/// W <c>LIKE</c> procent znaczy „cokolwiek", a podkreślenie „jeden dowolny znak".
/// Wpisane wprost do wzorca przestają być tym, co ktoś wpisał w pole szukania: sam
/// procent dawał wszystko, a „100_" nie znajdowało „100_netto", za to znajdowało
/// „100 netto". Przy polu, do którego wpisuje się cokolwiek, to nie jest możliwość
/// — to jest pomyłka czekająca na swój znak.
/// </para>
/// <para>
/// Odwrotny ukośnik jako znak wyłączający, bo w szukanym tekście zdarza się najrzadziej
/// z całej trójki — a wyłączany jest też on sam, więc szukanie ukośnika działa.
/// </para>
/// </remarks>
public static class LikePattern
{
    /// <summary>Znak wyłączający — ten sam trzeba podać zapytaniu po słowie ESCAPE.</summary>
    public const string Escape = "\\";

    public static string Containing(string text) => $"%{Quiet(text)}%";

    private static string Quiet(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
