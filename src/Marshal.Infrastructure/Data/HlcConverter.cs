using Marshal.Domain.Primitives;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Marshal.Infrastructure.Data;

/// <summary>
/// Zegar logiczny w bazie jako tekst „czas.licznik.urządzenie" — czytelny przy
/// diagnostyce i identyczny z postacią w logu synchronizacji (spec 9.3).
/// </summary>
public sealed class HlcConverter : ValueConverter<Hlc, string>
{
    /// <summary>
    /// Postać tekstowa wartości domyślnej — jedyny tekst, który ta przejściówka
    /// przyjmuje, a którego <see cref="Hlc.Parse"/> odrzuca.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HLC bez identyfikatora urządzenia nie jest HLC i dziedzina ma prawo go odrzucać:
    /// ten człon domyka porządek, więc bez niego dwa urządzenia nie rozstrzygną remisu.
    /// Pusty wychodzi wyłącznie z wartości domyślnej struktury, która nie pochodzi
    /// znikąd — nikt jej nie zapisał.
    /// </para>
    /// <para>
    /// EF pyta jednak o nią sam. Wartość domyślna jest dla niego <b>wartownikiem</b>:
    /// tym, z czym porównuje pole, żeby rozstrzygnąć, czy ktoś je ustawił. W modelu
    /// budowanym z kodu wartownik leży po stronie zwykłych typów i nikt go nie przepuszcza
    /// przez przejściówkę; w modelu skompilowanym leży zapisany w postaci bazodanowej
    /// (<c>SetSentinelFromProviderValue</c>) i wraca **przez nią**. Bez tej linijki
    /// pierwsze dotknięcie encji kończyło się wyjątkiem o nieprawidłowej postaci HLC —
    /// 294 testy z 618.
    /// </para>
    /// <para>
    /// Odpowiedź należy więc tutaj, a nie w dziedzinie: to przejściówka ma obowiązek
    /// oddać z powrotem to, co sama zapisała, łącznie z wartością domyślną.
    /// </para>
    /// </remarks>
    private static readonly string Default = default(Hlc).ToString();

    public HlcConverter()
        : base(hlc => hlc.ToString(), text => FromText(text))
    {
    }

    private static Hlc FromText(string text) =>
        text == Default ? default : Hlc.Parse(text);
}
