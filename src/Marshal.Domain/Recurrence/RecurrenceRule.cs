using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marshal.Domain.Recurrence;

/// <summary>
/// Reguła powtarzania (spec 5.7). Typ własny, nie RRULE.
/// </summary>
/// <remarks>
/// <para>
/// RRULE z iCal odrzucony celowo: obsługuje przypadki, których nigdy nie użyjesz,
/// a nie ma pojęcia <see cref="Anchor"/> ani <see cref="OnMissed"/> — czyli dokładnie
/// tego, co jest tu istotne.
/// </para>
/// <para>
/// <b>Jedna kolumna, nie własność złożona.</b> Reguła trafia do bazy jako jeden tekst
/// JSON. Gdyby EF rozłożył ją na osiem kolumn, byłoby osiem osobnych pól w dzienniku
/// zmian, a scalanie per pole potrafiłoby złożyć rytm z połówek dwóch różnych decyzji:
/// dni tygodnia z telefonu i odstęp z komputera. Reguła jest **jedną decyzją**, więc
/// wygrywa albo przegrywa w całości.
/// </para>
/// </remarks>
public sealed record RecurrenceRule
{
    /// <summary>Wartość <see cref="DayOfMonth"/> znacząca „ostatni dzień miesiąca".</summary>
    /// <remarks>
    /// Osobnego pola nie ma, bo dzień miesiąca jest zawsze przycinany do długości
    /// miesiąca: 31 w lutym daje 28 albo 29. „Ostatniego" to po prostu 31.
    /// </remarks>
    public const int LastDay = 31;

    /// <summary>
    /// Wyliczenia jako nazwy, nie liczby. Reguła siedzi w bazie i w dzienniku zmian
    /// jako tekst; <c>"Weekly"</c> da się przeczytać przy diagnostyce, a <c>2</c> nie —
    /// i nie rozjedzie się, gdy do wyliczenia dojdzie kiedyś wartość w środku.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public RecurrenceRule(
        RecurrenceKind kind,
        int interval = 1,
        Weekdays daysOfWeek = Weekdays.None,
        int? dayOfMonth = null,
        RecurrenceAnchor? anchor = null,
        OnMissed onMissed = OnMissed.Carry,
        DateOnly? until = null,
        int? count = null)
    {
        if (interval < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Odstęp musi być dodatni.");
        }

        if (kind == RecurrenceKind.Weekly && daysOfWeek == Weekdays.None)
        {
            throw new ArgumentException(
                "Powtarzanie tygodniowe bez wskazanego dnia nie ma kiedy wypaść.", nameof(daysOfWeek));
        }

        if (dayOfMonth is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(dayOfMonth), "Dzień miesiąca mieści się w 1–31.");
        }

        if (count is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Liczba wystąpień musi być dodatnia.");
        }

        Kind = kind;
        Interval = interval;
        DaysOfWeek = daysOfWeek;
        DayOfMonth = dayOfMonth;
        Anchor = anchor ?? DefaultAnchorFor(kind);
        OnMissed = onMissed;
        Until = until;
        Count = count;
    }

    public RecurrenceKind Kind { get; }

    public int Interval { get; }

    public Weekdays DaysOfWeek { get; }

    /// <summary>1–31, zawsze przycinane do długości miesiąca. Puste = dzień daty bazowej.</summary>
    public int? DayOfMonth { get; }

    public RecurrenceAnchor Anchor { get; }

    public OnMissed OnMissed { get; }

    /// <summary>Ostatni dzień, w którym wolno wypaść wystąpieniu.</summary>
    public DateOnly? Until { get; }

    /// <summary>
    /// Ile wystąpień **jeszcze** zostało, licząc z bieżącym. Puste = bez końca.
    /// </summary>
    /// <remarks>
    /// Licznik pozostałych, nie łączna liczba od początku: każde wystąpienie dostaje
    /// regułę pomniejszoną o jeden. Dzięki temu stan serii siedzi w samym wystąpieniu
    /// i nie wymaga liczenia historii — co przy synchronizacji, gdzie historia bywa
    /// niekompletna, byłoby zawodne.
    /// </remarks>
    public int? Count { get; }

    /// <summary>
    /// Domyślne zaczepienie **liczone z rodzaju**, nie stałe (spec 5.7).
    /// </summary>
    /// <remarks>
    /// Przy tygodniowym, miesięcznym i rocznym nazywasz konkretny dzień („poniedziałek",
    /// „15-go") — to z definicji rytm narzucony z zewnątrz. „Co 3 dni" nie ma zaczepienia
    /// w świecie; gdyby miało, powiedziałabyś „w poniedziałki i czwartki".
    /// </remarks>
    public static RecurrenceAnchor DefaultAnchorFor(RecurrenceKind kind) => kind switch
    {
        RecurrenceKind.Daily or RecurrenceKind.EveryNDays => RecurrenceAnchor.FromCompletion,
        _ => RecurrenceAnchor.FromScheduled,
    };

    /// <summary>Reguła na kolejne wystąpienie: to samo, o jedno wystąpienie mniej.</summary>
    /// <remarks>
    /// Nowa reguła budowana konstruktorem, nie wyrażeniem <c>with</c>: dzięki temu
    /// sprawdzenia przechodzą także tutaj. Przy wyczerpanej serii zwraca siebie —
    /// kolejne wystąpienie i tak nie powstanie.
    /// </remarks>
    public RecurrenceRule Advance() =>
        Count is null or <= 1
            ? this
            : new RecurrenceRule(
                Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until, Count - 1);

    public string ToJson() =>
        JsonSerializer.Serialize(
            new Wire(Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until, Count), Json);

    /// <summary>Zwraca <c>null</c> zamiast rzucać: zapis z nowszej wersji aplikacji nie
    /// może wywrócić scalania (spec 9.4).</summary>
    public static RecurrenceRule? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Wire>(json, Json) is { } w
                ? new RecurrenceRule(
                    w.Kind, w.Interval, w.DaysOfWeek, w.DayOfMonth,
                    w.Anchor, w.OnMissed, w.Until, w.Count)
                : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Postać zapisu, oddzielona od typu domenowego.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Powód bezpośredni: konstruktor domenowy przyjmuje zaczepienie jako wartość pustą
    /// („wylicz z rodzaju"), a właściwość pustej nie dopuszcza — czego serializator nie
    /// umie pogodzić.
    /// </para>
    /// <para>
    /// Powód istotniejszy: reguły leżą w bazie i w dzienniku zmian. Osobna postać zapisu
    /// znaczy, że typ domenowy wolno zmieniać bez unieważniania tego, co już zapisane —
    /// a odczyt przechodzi przez konstruktor, więc **sprawdzenia obowiązują także
    /// wartości, które przyszły z pliku**, nie tylko te wpisane w aplikacji.
    /// </para>
    /// </remarks>
    private sealed record Wire(
        RecurrenceKind Kind,
        int Interval,
        Weekdays DaysOfWeek,
        int? DayOfMonth,
        RecurrenceAnchor Anchor,
        OnMissed OnMissed,
        DateOnly? Until,
        int? Count);
}
