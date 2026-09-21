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
        int? count = null,
        IReadOnlyList<RecurrenceChange>? changes = null,
        int? minutes = null,
        TimeOnly? time = null)
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
        Minutes = minutes is > 0 ? minutes : null;
        Time = time;

        // Po jednej zmianie na dzień i w kolejności dni. Dwie zmiany tego samego
        // wystąpienia znaczyłyby, że trzeba wiedzieć, która jest nowsza — a to jest
        // wiedza, której zapis nie niesie. Wygrywa ostatnia, tak samo jak przy warunkach
        // filtra: zapis z nowszej wersji aplikacji ma się otworzyć, a nie wywrócić.
        Changes = changes is null or { Count: 0 }
            ? []
            : [.. changes.GroupBy(z => z.Date).Select(g => g.Last()).OrderBy(z => z.Date)];
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
    /// Wystąpienia odwołane i przełożone — po jednym wpisie na dzień z reguły.
    /// </summary>
    /// <remarks>
    /// <para>
    /// W regule, a nie w osobnej tabeli. Trzy powody. Reguła jedzie z wystąpienia na
    /// wystąpienie razem z serią, więc zmiany jadą z nią i nie trzeba ich z niczym
    /// wiązać. Siedzi w jednej kolumnie JSON, więc dopisanie ich nie zmienia kształtu
    /// bazy. I nie wymaga tożsamości serii — a ta w modelu, w którym żyje naraz jedno
    /// wystąpienie, musiałaby dopiero powstać.
    /// </para>
    /// <para>
    /// Cena jest jedna i świadoma: reguła wygrywa albo przegrywa w całości, więc zmiana
    /// jednego wystąpienia zrobiona na telefonie przepada, jeżeli w tej samej chwili
    /// zmieni się rytm na komputerze. Przy dwóch urządzeniach jednej osoby to zbieg
    /// rzadki, a rozdzielenie kosztowałoby osobny byt do scalania.
    /// </para>
    /// <para>
    /// Lista nie rośnie bez końca: przy każdym przejściu na kolejne wystąpienie odpada
    /// wszystko, co dotyczyło dni już minionych — zob. <see cref="Advance"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<RecurrenceChange> Changes { get; }

    /// <summary>
    /// Ile trwa wystąpienie tej serii, w minutach. Puste znaczy „tyle, co bieżące".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zadanie niosące rytm jest jednocześnie <b>jednym wystąpieniem</b> i <b>wzorcem
    /// serii</b>, a to są dwie różne rzeczy o tej samej długości tylko z pozoru.
    /// Przeciągnięcie dolnej krawędzi dzisiejszego bloku znaczy „dziś siedzę dłużej",
    /// a nie „od zawsze to trwa dłużej" — a bez tego pola znaczyło jedno i drugie naraz,
    /// bo rysowanie zapowiedzi sięgało po długość właśnie do tamtego zadania.
    /// </para>
    /// <para>
    /// Zapisywana przy zmianie z karty zadania: tam wpisuje się, czym rzecz <b>jest</b>,
    /// i to jest decyzja o całej serii. Siatka służy do jednego dnia i zostawia serię
    /// w spokoju. Puste pole u rytmów założonych wcześniej znaczy tyle, co dawniej —
    /// długość bierze się wtedy z wystąpienia niosącego regułę.
    /// </para>
    /// </remarks>
    public int? Minutes { get; }

    /// <summary>
    /// O której wypada wystąpienie tej serii. Puste znaczy „o tej, co bieżące".
    /// </summary>
    /// <remarks>
    /// Ta sama sprawa, co przy długości, i z tego samego powodu: zadanie niosące rytm
    /// jest jednocześnie jednym wystąpieniem i wzorcem serii. Przeciągnięcie dzisiejszego
    /// bloku o godzinę w dół znaczy „dziś zaczynam później", a nie „od teraz zaczynam
    /// później" — a bez tego pola znaczyło jedno i drugie naraz, bo zapowiedzi sięgały
    /// po porę właśnie do tamtego zadania.
    /// </remarks>
    public TimeOnly? Time { get; }

    /// <summary>Zmiana dotycząca wskazanego dnia z reguły, jeśli jest.</summary>
    public RecurrenceChange? ChangeOn(DateOnly date) =>
        Changes.Count == 0 ? null : Changes.FirstOrDefault(z => z.Date == date);

    /// <summary>Ta sama reguła z dopisaną albo zastąpioną zmianą jednego wystąpienia.</summary>
    public RecurrenceRule With(RecurrenceChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new RecurrenceRule(
            Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until, Count,
            [.. Changes.Where(z => z.Date != change.Date), change], Minutes, Time);
    }

    /// <summary>Ta sama reguła bez zmiany dotyczącej wskazanego dnia.</summary>
    public RecurrenceRule Without(DateOnly date) =>
        new(Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until, Count,
            [.. Changes.Where(z => z.Date != date)], Minutes, Time);

    /// <summary>
    /// Ta sama reguła z zapamiętaną długością wystąpienia.
    /// </summary>
    /// <remarks>
    /// Wołane wtedy, gdy zmienia się długość <b>bieżącego</b> wystąpienia rytmu, który
    /// swojej długości jeszcze nie pamięta: seria zapamiętuje to, czym była do tej pory,
    /// zanim jedno wystąpienie zrobi się inne. Bez tego kroku zapowiedzi poszłyby za
    /// zmianą, bo długość brałyby z tego właśnie zadania.
    /// </remarks>
    public RecurrenceRule WithLength(int? minutes) =>
        new(Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until, Count,
            Changes, minutes, Time);

    /// <summary>Ta sama reguła z zapamiętaną porą wystąpienia.</summary>
    /// <remarks>
    /// Wołane przy przełożeniu <b>bieżącego</b> wystąpienia rytmu, który swojej pory
    /// jeszcze nie pamięta — zob. <see cref="WithLength"/>, to jest ta sama zasada.
    /// </remarks>
    public RecurrenceRule WithHour(TimeOnly? time) =>
        new(Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until, Count,
            Changes, Minutes, time);

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
    /// <param name="after">
    /// Dzień właśnie zużytego wystąpienia. Zmiany dotyczące jego i wcześniejszych dni
    /// odpadają: zostały już zastosowane albo minęły, a lista, z której nic nie znika,
    /// po roku rytmu codziennego byłaby dłuższa od samej reguły.
    /// </param>
    public RecurrenceRule Advance(DateOnly? after = null)
    {
        var left = after is { } used
            ? Changes.Where(z => z.Date > used).ToList()
            : Changes;

        // Bez zmian do odcięcia i bez licznika do pomniejszenia nie ma czego przepisywać.
        if (Count is null or <= 1 && left.Count == Changes.Count)
        {
            return this;
        }

        return new RecurrenceRule(
            Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until,
            Count is null or <= 1 ? Count : Count - 1,
            left, Minutes, Time);
    }

    public string ToJson() =>
        JsonSerializer.Serialize(
            new Wire(
                Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until, Count,
                Changes.Count == 0 ? null : Changes, Minutes, Time),
            Json);

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
                    w.Anchor, w.OnMissed, w.Until, w.Count, w.Changes, w.Minutes, w.Time)
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
        int? Count,
        IReadOnlyList<RecurrenceChange>? Changes = null,
        int? Minutes = null,
        TimeOnly? Time = null);

    /// <summary>
    /// Porównanie po treści, także listy zmian.
    /// </summary>
    /// <remarks>
    /// Rekord porównuje listę przez odwołanie, więc reguła zapisana i odczytana z powrotem
    /// nie byłaby sobie równa — a na tym stoi sprawdzenie zapisu i odczytu. Skrót pomija
    /// zmiany celowo: równe reguły mają wtedy równe skróty, a to jedyne, czego skrót
    /// musi dotrzymać.
    /// </remarks>
    public bool Equals(RecurrenceRule? other) =>
        other is not null
        && Kind == other.Kind
        && Interval == other.Interval
        && DaysOfWeek == other.DaysOfWeek
        && DayOfMonth == other.DayOfMonth
        && Anchor == other.Anchor
        && OnMissed == other.OnMissed
        && Until == other.Until
        && Count == other.Count
        && Minutes == other.Minutes
        && Time == other.Time
        && Changes.SequenceEqual(other.Changes);

    public override int GetHashCode() =>
        HashCode.Combine(Kind, Interval, DaysOfWeek, DayOfMonth, Anchor, OnMissed, Until, Count);
}
