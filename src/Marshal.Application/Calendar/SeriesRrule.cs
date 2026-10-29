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
/// <b>Dni wyjęte z rytmu jadą razem z regułą</b>, jako <c>EXDATE</c>. Droga przez samo
/// wystąpienie — odwołanie go w Google po jego identyfikatorze — wyglądała czyściej
/// i jest w tym modelu gorsza: identyfikator wystąpienia nadaje Google, więc trzeba by
/// go najpierw od niego wydobyć, a dwa urządzenia pomijające ten sam dzień wysłałyby
/// dwa osobne odwołania w nieokreślonej kolejności. <c>EXDATE</c> jest przeciwnie
/// <b>funkcją tego, co u nas stoi</b>: oba urządzenia wyliczają tę samą listę dni
/// z tych samych nagrobków i wysyłają dokładnie to samo pole.
/// </para>
/// <para>
/// Czego ta reguła nadal <b>nie</b> niesie: dni przestawionych z ręki na inną porę
/// albo inny dzień. Te stoją w Google tam, gdzie wypadały z rytmu.
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
    /// Strefa okna — do policzenia <c>UNTIL</c> i <c>EXDATE</c>. Reguła iCal wymaga
    /// tam chwili w czasie uniwersalnym, gdy wydarzenie ma godzinę, a samej daty, gdy
    /// jest całodniowe. Pomylenie tych dwóch postaci Google odrzuca.
    /// </param>
    /// <param name="cancelled">
    /// Dni, których w serii nie ma, choć rytm je wyznacza — pominięte i odwołane.
    /// Puste znaczy „rytm bez wyjątków".
    /// </param>
    public static IReadOnlyList<string>? Lines(
        RecurrenceRule rule,
        DateOnly? last,
        TimeOnly? time,
        TimeZoneInfo zone,
        IReadOnlyCollection<DateOnly>? cancelled = null)
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

        var lines = new List<string>(2) { $"RRULE:{string.Join(';', parts)}" };

        if (cancelled is { Count: > 0 })
        {
            lines.Add(Excluded(cancelled, time, zone));
        }

        return lines;
    }

    /// <summary>
    /// Dni wyjęte z rytmu — jedna linia <c>EXDATE</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Postać jak przy <c>UNTIL</c> i z tego samego powodu: sama data przy wydarzeniu
    /// całodniowym, chwila w czasie uniwersalnym przy wydarzeniu z godziną. Chwila
    /// liczona z przesunięcia strefy <b>tego dnia</b>, bo wyjęcie musi trafić
    /// w wystąpienie co do sekundy — po zmianie czasu dzień wyjęty z przesunięciem
    /// letnim nie trafiłby w nic i wystąpienie zostałoby w kalendarzu.
    /// </para>
    /// <para>
    /// Jedna linia z listą dat, nie linia na dzień: reguła iCal dopuszcza oba zapisy,
    /// a pole wysyłane jest w całości przy każdym zapisie, więc liczba linii jest
    /// wyłącznie kosztem.
    /// </para>
    /// </remarks>
    private static string Excluded(
        IReadOnlyCollection<DateOnly> days, TimeOnly? time, TimeZoneInfo zone)
    {
        // Uporządkowane i bez powtórzeń, żeby to samo wejście dawało zawsze ten sam
        // napis: inaczej dwa urządzenia z tą samą wiedzą wysyłałyby różne pola
        // i każde widziałoby zmianę tam, gdzie jej nie ma.
        var sorted = days.Distinct().Order().ToList();

        if (time is not { } at)
        {
            return "EXDATE;VALUE=DATE:" + string.Join(
                ',', sorted.Select(z => z.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        }

        return "EXDATE:" + string.Join(',', sorted.Select(z => Moment(z, at, zone)));
    }

    /// <summary>
    /// Strefa w postaci, którą rozumie Google — nazwą IANA. Puste, gdy się nie da.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wydarzenie cykliczne bez strefy Google odrzuca: <c>Missing time zone definition
    /// for start time</c>. I ma rację — rozwinięcie reguły wymaga wiedzy, czym jest
    /// „ósma rano" po zmianie czasu, a sama chwila z przesunięciem zna tylko to jedno
    /// przesunięcie, w którym ją policzono. Bez strefy seria po ostatniej niedzieli
    /// października wypadałaby godzinę obok.
    /// </para>
    /// <para>
    /// Windows nazywa strefy po swojemu („Central European Standard Time") i tej nazwy
    /// Google nie zna. Przekład jest w .NET; gdyby się nie udał, lepiej nie wysłać nic
    /// niż wysłać serię rozwijającą się w złej porze — stąd pustka zamiast zgadywania.
    /// </para>
    /// </remarks>
    public static string? IanaName(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
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

        return Moment(last, at, zone);
    }

    /// <summary>Dzień i pora jako chwila w czasie uniwersalnym, w zapisie iCal.</summary>
    private static string Moment(DateOnly day, TimeOnly at, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(at);
        var moment = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();

        return moment.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }
}
