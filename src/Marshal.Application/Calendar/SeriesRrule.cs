using System.Globalization;
using Marshal.Domain.Recurrence;

namespace Marshal.Application.Calendar;

/// <summary>
/// Rytm serii zapisany regułą powtarzania iCal — tak, jak rozumie ją Google.
/// </summary>
/// <remarks>
/// <para>
/// Jedno wydarzenie cykliczne na serię, a nie sześćdziesiąt osobnych. Osobne byłyby
/// sześćdziesięcioma wyprawami po sieci na każdą serię przy pierwszym postawieniu okna,
/// sześćdziesięcioma poprawkami przy zmianie nazwy i — najgorsze — <b>dwoma</b>
/// wydarzeniami na ten sam dzień, bo pulpit i telefon stawiają okno niezależnie, a
/// dopóki wskazanie na wydarzenie nie dojedzie synchronizacją, oba utworzyłyby własne.
/// Wydarzenie cykliczne jest jedno, więc to pytanie znika razem z nim: rozwijaniem
/// powtórzeń zajmuje się Google, tak samo jak przy odczycie.
/// </para>
/// <para>
/// <b>Nie każdy rytm da się tak zapisać</b> i wtedy oddawane jest nic. Reguła iCal nie
/// zna zaczepienia na wykonaniu — „co 3 dni od wykonania" nie ma dat, dopóki poprzednie
/// nie zostanie odhaczone, więc nie ma czego wysłać. Licznik pozostałych wystąpień też
/// nie przechodzi wprost: <c>COUNT</c> liczy od początku serii, a nasz licznik maleje
/// z każdym odhaczeniem, więc zamiast niego jedzie <c>UNTIL</c> policzone z ostatniego
/// dnia, który okno wylicza.
/// </para>
/// <para>
/// Czego ta reguła <b>nie</b> niesie: dni odwołanych i zmienionych z ręki. Odwołanie
/// jednego wystąpienia robi się w Google przez samo wystąpienie, nie przez regułę,
/// i to jest osobna droga — zob. ISeriesMirror.
/// </para>
/// </remarks>
public static class SeriesRrule
{
    /// <summary>
    /// Linie powtarzalności dla jednego wydarzenia — albo <c>null</c>, gdy tego rytmu
    /// nie da się zapisać regułą.
    /// </summary>
    /// <param name="last">
    /// Ostatni dzień serii: data końca z reguły albo ostatni dzień, który wylicza okno
    /// serii liczonej na wystąpienia. Puste znaczy „bez końca".
    /// </param>
    /// <param name="time">Pora dnia serii. Puste znaczy wydarzenie całodniowe.</param>
    /// <param name="zone">
    /// Strefa okna — do policzenia <c>UNTIL</c>. Reguła iCal wymaga tam chwili
    /// w czasie uniwersalnym, gdy wydarzenie ma godzinę, a samej daty, gdy jest
    /// całodniowe. Pomylenie tych dwóch postaci Google odrzuca.
    /// </param>
    public static IReadOnlyList<string>? Lines(
        RecurrenceRule rule, DateOnly? last, TimeOnly? time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(zone);

        if (rule.Anchor == RecurrenceAnchor.FromCompletion)
        {
            return null;
        }

        var parts = new List<string> { $"FREQ={Frequency(rule.Kind)}" };

        if (rule.Interval > 1)
        {
            parts.Add($"INTERVAL={rule.Interval}");
        }

        if (rule.Kind == RecurrenceKind.Weekly)
        {
            parts.Add($"BYDAY={Days(rule.DaysOfWeek)}");

            // Tydzień zaczyna się w poniedziałek — tak liczy rytm u nas i tak ma liczyć
            // Google. Bez tego „co drugi tydzień" rozjeżdżałoby się o tydzień tam, gdzie
            // tygodnie dzieli się od niedzieli, bo granica tygodnia wypadałaby o dzień
            // wcześniej i wystąpienie z poniedziałku należałoby do poprzedniego tygodnia.
            parts.Add("WKST=MO");
        }

        if (rule.Kind == RecurrenceKind.Monthly && rule.DayOfMonth is { } day)
        {
            // Trzydziesty pierwszy znaczy u nas „ostatniego": dzień miesiąca jest zawsze
            // przycinany do jego długości. W regule iCal to jest −1, bo samo 31 znaczyłoby
            // „tylko w miesiącach, które mają 31 dni" — czyli rytm pomijający luty.
            parts.Add($"BYMONTHDAY={(day >= RecurrenceRule.LastDay ? "-1" : day.ToString(CultureInfo.InvariantCulture))}");
        }

        if (last is { } end)
        {
            parts.Add($"UNTIL={Until(end, time, zone)}");
        }

        return [$"RRULE:{string.Join(';', parts)}"];
    }

    private static string Frequency(RecurrenceKind kind) => kind switch
    {
        RecurrenceKind.Daily or RecurrenceKind.EveryNDays => "DAILY",
        RecurrenceKind.Weekly => "WEEKLY",
        RecurrenceKind.Monthly => "MONTHLY",
        _ => "YEARLY",
    };

    /// <summary>Dni tygodnia skrótami iCal, w kolejności tygodnia.</summary>
    private static string Days(Weekdays set)
    {
        var names = new List<string>(7);

        if (set.Includes(DayOfWeek.Monday)) { names.Add("MO"); }
        if (set.Includes(DayOfWeek.Tuesday)) { names.Add("TU"); }
        if (set.Includes(DayOfWeek.Wednesday)) { names.Add("WE"); }
        if (set.Includes(DayOfWeek.Thursday)) { names.Add("TH"); }
        if (set.Includes(DayOfWeek.Friday)) { names.Add("FR"); }
        if (set.Includes(DayOfWeek.Saturday)) { names.Add("SA"); }
        if (set.Includes(DayOfWeek.Sunday)) { names.Add("SU"); }

        // Pusty zbiór konstruktor reguły odrzuca, ale reguła może przyjść z nowszej
        // wersji aplikacji przez synchronizację. Zepsuta reguła ma dać zły rytm,
        // a nie wydarzenie, którego Google nie przyjmie.
        return names.Count > 0 ? string.Join(',', names) : "MO";
    }

    /// <summary>
    /// Koniec rytmu w postaci, której wymaga reguła iCal.
    /// </summary>
    /// <remarks>
    /// Postać musi zgadzać się z postacią początku wydarzenia: sama data przy
    /// całodniowym, chwila w czasie uniwersalnym przy wydarzeniu z godziną. Chwila
    /// policzona jest z <b>przesunięcia strefy tego dnia</b>, a nie dzisiejszego —
    /// inaczej rytm kończący się po zmianie czasu gubiłby albo dokładał jeden dzień.
    /// </remarks>
    private static string Until(DateOnly last, TimeOnly? time, TimeZoneInfo zone)
    {
        if (time is not { } at)
        {
            return last.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        var local = last.ToDateTime(at);
        var moment = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();

        return moment.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }
}
