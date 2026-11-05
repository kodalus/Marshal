using System.Globalization;
using Marshal.Application.Abstractions;
using Marshal.Domain.Lunar;

namespace Marshal.Application.Calendar;

/// <summary>
/// Data księżycowa gotowa do narysowania: liczby zamienione na podpis.
/// </summary>
/// <param name="Month">Numer miesiąca księżycowego.</param>
/// <param name="Day">Dzień tego miesiąca.</param>
/// <param name="Length">Długość miesiąca: 29 albo 30.</param>
/// <param name="MonthName">Nazwa miesiąca albo „Luna N”, gdy nazw nie ma.</param>
/// <param name="IsNewMoon">Czy to dzień nowiu — pierwszy dzień miesiąca.</param>
/// <param name="IsFullMoon">Czy to dzień pełni.</param>
/// <param name="IsThirteenth">Czy to miesiąc trzynasty, dodatkowy.</param>
/// <remarks>
/// Osobny typ od <see cref="LunarDate"/>, bo tamten jest wynikiem rachunku, a ten
/// podpisem — i mają różne czasy życia. Rachunek nie wie nic o nazwach ani o tym, czy
/// użytkownik je włączył; podpis wie o jednym i drugim, a nie wie, jak się go policzyło.
/// </remarks>
public sealed record LunarMark(
    int Month,
    int Day,
    int Length,
    string MonthName,
    bool IsNewMoon,
    bool IsFullMoon,
    bool IsThirteenth)
{
    /// <summary>
    /// Znak fazy: wypełnione kółko na nów, puste na pełnię, nic w pozostałe dni.
    /// </summary>
    /// <remarks>
    /// Nów wypełniony, pełnia pusta — odwrotnie niż to, co widać na niebie, i celowo:
    /// w siatce miesiąca znaki stoją na jasnym tle, więc <b>wypełniony jest tym, który
    /// widać mocniej</b>. Nów jest początkiem miesiąca, czyli tym, co ma się rzucać
    /// w oczy; pełnia jest w środku i wystarczy, że da się ją znaleźć wzrokiem.
    /// </remarks>
    public string Glyph => IsNewMoon ? "●" : IsFullMoon ? "○" : string.Empty;

    public bool HasGlyph => Glyph.Length > 0;

    /// <summary>
    /// Podpis do nagłówka kolumny: znak fazy i dzień miesiąca.
    /// </summary>
    /// <remarks>
    /// Bez nazwy i bez numeru miesiąca, bo kolumna ma na telefonie jedną siódmą ekranu
    /// szerokości — „Luna Listopada, dzień 27” zostawiłoby z tego trzy litery i kropki.
    /// Dzień miesiąca jest tą częścią, która zmienia się codziennie, więc ta zostaje;
    /// po nazwę miesiąca idzie się do widoku miesiąca albo na kartę zadania.
    /// </remarks>
    public string Mark => HasGlyph
        ? $"{Glyph} {Day}"
        : Day.ToString(CultureInfo.CurrentCulture);

    /// <summary>Czy ten dzień otwiera miesiąc księżycowy — po tym rysuje się granicę.</summary>
    public bool Opens => IsNewMoon;

    /// <summary>Pełny podpis: tam, gdzie jest na niego miejsce.</summary>
    public string Full => $"{MonthName}, dzień {Day} z {Length}";
}

/// <summary>
/// Podpisy księżycowe — jedno miejsce, w którym rachunek spotyka się z ustawieniami.
/// </summary>
/// <remarks>
/// <para>
/// Jedna funkcja na całą aplikację, nie po jednej na ekran. Kalendarz, siatka miesiąca
/// i karta zadania pytają o to samo i mają odpowiadać tak samo — a rozsypane po
/// ekranach formatowanie rozjeżdża się po pierwszej poprawce w jednym z nich.
/// </para>
/// <para>
/// To tutaj mieszka też odpowiedź „nie pokazuj nic”: gdy kalendarz księżycowy jest
/// wyłączony, <see cref="Of"/> oddaje <see langword="null"/>, a ekrany pytają tylko
/// o to, czy jest co pokazać. Dzięki temu żaden z nich nie zna ustawień i żaden nie
/// musi pamiętać o drugim przełączniku.
/// </para>
/// </remarks>
public sealed class LunarLabels(ISettings settings)
{
    /// <summary>Czy kalendarz księżycowy jest w ogóle włączony na tym urządzeniu.</summary>
    public bool Shown => settings.LunarShown;

    /// <summary>
    /// Podpis dnia <paramref name="day"/>, albo <see langword="null"/>, gdy wyłączony.
    /// </summary>
    public LunarMark? Of(DateOnly day)
    {
        if (!settings.LunarShown)
        {
            return null;
        }

        var date = LunarCalendar.Of(day, settings.Zone);

        // Nazwy tylko wtedy, gdy włączone — i tylko te wpisane. Pusta nazwa nie jest
        // usterką: tak wygląda pole, które ktoś wyczyścił, i zostaje po niej numer.
        var names = settings.LunarNamesShown ? settings.LunarMonthNames : null;

        return new LunarMark(
            date.Month,
            date.Day,
            date.Length,
            LunarNames.Of(names, date.Month) ?? $"Luna {date.Month}",
            date.IsNewMoon,
            date.IsFullMoon,
            date.IsThirteenth);
    }

    /// <summary>Pełny podpis dnia, albo <see langword="null"/>, gdy wyłączony.</summary>
    public string? Full(DateOnly day) => Of(day)?.Full;
}
