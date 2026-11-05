namespace Marshal.Domain.Lunar;

/// <summary>
/// Nazwy miesięcy księżycowych — warstwa podpisu, nie element rachunku.
/// </summary>
/// <remarks>
/// <para>
/// Kalendarz liczy numerami i tylko numerami. Nazwa jest tym, co widać, a widać ją
/// wtedy, gdy użytkownik ją włączy; wyłączenie nazw nie może niczego policzyć inaczej
/// i nie może nazw skasować. Dlatego ten typ nie wie nic o ustawieniach: dostaje listę
/// albo jej nie dostaje.
/// </para>
/// <para>
/// Trzynasty miesiąc ma nazwę tak samo jak pozostałe, choć w większości lat go nie ma.
/// Alternatywą byłoby doklejanie nazwy dopiero w roku trzynastomiesięcznym — czyli
/// lista, która raz ma dwanaście pozycji, a raz trzynaście, i ustawienia, które w zależności
/// od roku pokazują inną liczbę pól. Stała trzynastka jest prostsza do wytłumaczenia
/// i do zapisania.
/// </para>
/// </remarks>
public static class LunarNames
{
    /// <summary>Ile nazw ma lista: trzynaście, bo tyle miesięcy ma najdłuższy rok.</summary>
    public const int Count = 13;

    /// <summary>
    /// Propozycja nazw po polsku — do wstawienia jednym przyciskiem i do poprawienia ręcznie.
    /// </summary>
    /// <remarks>
    /// Nazwy idą za porą roku, bo rok księżycowy zaczyna się po przesileniu zimowym:
    /// pierwszy miesiąc wypada w styczniu, dziesiąty w okolicach listopada. Przy roku
    /// trzynastomiesięcznym przesuwają się o jeden miesiąc — i to jest cena nazw
    /// stałych, świadoma: „Luna Progu” na końcu zamyka rok niezależnie od tego,
    /// w którym miesiącu gregoriańskim wypadnie.
    /// </remarks>
    public static IReadOnlyList<string> Preset { get; } =
    [
        "Luna Mrozu",
        "Luna Śniegu",
        "Luna Przebudzenia",
        "Luna Kiełków",
        "Luna Kwitnienia",
        "Luna Światła",
        "Luna Miodu",
        "Luna Żniw",
        "Luna Owoców",
        "Luna Listopada",
        "Luna Mgieł",
        "Luna Ciszy",
        "Luna Progu",
    ];

    /// <summary>
    /// Nazwa miesiąca <paramref name="month"/> z listy <paramref name="names"/>, albo
    /// <see langword="null"/>, gdy listy nie ma albo nie ma w niej tej pozycji.
    /// </summary>
    /// <remarks>
    /// Brak nazwy nie jest usterką: tak wygląda kalendarz z wyłączonymi nazwami i tak
    /// samo wygląda lista, w której ktoś wyczyścił jedno pole. W obu przypadkach zostaje
    /// numer, który jest zawsze.
    /// </remarks>
    public static string? Of(IReadOnlyList<string>? names, int month)
    {
        if (names is null || month < 1 || month > names.Count)
        {
            return null;
        }

        var name = names[month - 1];

        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// Lista <paramref name="names"/> dociągnięta albo przycięta do <see cref="Count"/> pozycji.
    /// </summary>
    /// <remarks>
    /// Ustawienia są zapisem urządzenia, a zapis może pochodzić z wersji, która miała
    /// inną liczbę nazw, albo z ręcznej poprawki pliku. Lista krótsza lub dłuższa niż
    /// trzynaście pozycji nie ma prawa wysypać okna ustawień, więc kształt wyrównuje się
    /// przy wejściu, a nie przy każdym użyciu.
    /// </remarks>
    public static IReadOnlyList<string> Padded(IReadOnlyList<string>? names)
    {
        var padded = new string[Count];

        for (var index = 0; index < Count; index++)
        {
            padded[index] = names is not null && index < names.Count
                ? names[index]
                : string.Empty;
        }

        return padded;
    }
}
