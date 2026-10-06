using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;

namespace Marshal.Domain.Series;

/// <summary>
/// Seria powtarzalna: reguła i szablon, z których powstają wystąpienia.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tożsamość serii — rzecz, której w modelu nie było.</b> Do tej pory regułę nosiło
/// najnowsze wystąpienie, a kolejne brało identyfikator z poprzednika i z dnia:
/// <c>f(poprzednik, dzień)</c>. Funkcja była deterministyczna, ale <b>jej wejścia nie
/// były wspólne</b> — dzień, na który wypada następnik, zależy od tego, kiedy dane
/// urządzenie zostało otwarte. Telefon otwarty w poniedziałek i pulpit otwarty w środę
/// liczyły różne daty, więc różne identyfikatory, więc dwa zadania; od tej chwili
/// łańcuchy były rozłączne na zawsze, a reguła sprowadzała oba z powrotem na te same
/// dni. Stąd „po dwa wystąpienia na dzień", które wracało po każdym sprzątnięciu.
/// </para>
/// <para>
/// Tutaj identyfikator wystąpienia liczy się z <b>serii i dnia</b> — a to są fakty
/// o serii, nie o historii żadnego urządzenia. Dwa urządzenia wyliczają te same
/// wiersze z tymi samymi identyfikatorami, więc scalanie składa je w jedno z definicji.
/// Podwojenie przestaje być błędem do naprawiania i staje się niemożliwe.
/// </para>
/// <para>
/// <b>Reguła i szablon jako dwie kolumny tekstu.</b> Ta sama decyzja, co przy regule
/// do tej pory i z tego samego powodu: jedna i druga jest <b>jedną decyzją</b>, więc
/// ma wygrywać albo przegrywać w całości. Rozłożone na kolumny dawałyby scalanie per
/// pole, które potrafi złożyć rytm z połówek dwóch różnych postanowień — dni tygodnia
/// z telefonu i odstęp z komputera.
/// </para>
/// <para>
/// <b>Seria nie jest zadaniem</b> i nie pokazuje się nigdzie na listach. Nie ma stanu,
/// nie da się jej odhaczyć ani wybrać na dziś. Jedyne, co robi, to mówi, jakie
/// wystąpienia mają istnieć.
/// </para>
/// </remarks>
public sealed class TaskSeries : Entity
{
    private TaskSeries()
    {
        RuleJson = string.Empty;
        TemplateJson = string.Empty;
    }

    private TaskSeries(
        Guid id,
        DateTimeOffset createdAt,
        Hlc updatedAt,
        DateOnly starts,
        RecurrenceRule rule,
        SeriesTemplate template)
        : base(id, createdAt, updatedAt)
    {
        Starts = starts;
        RuleJson = rule.ToJson();
        TemplateJson = template.ToJson();
    }

    /// <summary>
    /// Nowa seria. Tożsamość podaje wołający, gdy ma być <b>tą samą rzeczą</b> co na
    /// drugim urządzeniu.
    /// </summary>
    /// <remarks>
    /// Przeniesienie starego rytmu podaje tożsamość wyliczoną (<see cref="SeriesId"/>),
    /// bo odbywa się na każdym urządzeniu osobno i bez umawiania się. Nadanie rytmu
    /// z ręki podaje losową: dzieje się z jednego kliknięcia, w jednym miejscu, i nie
    /// ma bliźniaka.
    /// </remarks>
    public static TaskSeries Create(
        DateOnly starts,
        RecurrenceRule rule,
        SeriesTemplate template,
        DateTimeOffset now,
        Hlc stamp,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(template);

        return new TaskSeries(id ?? Guid.CreateVersion7(), now, stamp, starts, rule, template);
    }

    /// <summary>
    /// Dzień, od którego liczy się rytm.
    /// </summary>
    /// <remarks>
    /// Zaczepienie serii, nie jej pierwsze wystąpienie: „co drugi dzień od wtorku"
    /// potrzebuje wtorku także wtedy, gdy wtorkowe wystąpienie zostało skasowane.
    /// Bez tego pola rytm liczyłby się od najstarszego ocalałego wiersza i przesuwał
    /// się przy każdym skasowaniu — czyli kasowanie jednego dnia zmieniałoby wszystkie
    /// następne.
    /// </remarks>
    public DateOnly Starts { get; private set; }

    public string RuleJson { get; private set; }

    public string TemplateJson { get; private set; }

    /// <summary>
    /// Reguła, odczytana leniwie z <see cref="RuleJson"/>.
    /// </summary>
    /// <remarks>
    /// Odczyt pamiętany pod tekstem, z którego wyszedł — tak samo jak przy zadaniu.
    /// Pamiętanie pod flagą kłamałoby, gdy tekst zmieni się z boku, a zmienia się:
    /// scalanie synchronizacji wpisuje kolumnę wprost, omijając metody agregatu.
    /// </remarks>
    public RecurrenceRule Rule
    {
        get
        {
            if (_ruleFor != RuleJson)
            {
                _rule = RecurrenceRule.FromJson(RuleJson);
                _ruleFor = RuleJson;
            }

            return _rule ?? throw new InvalidOperationException(
                "Seria bez czytelnej reguły — zapis jest uszkodzony.");
        }
    }

    public SeriesTemplate Template
    {
        get
        {
            if (_templateFor != TemplateJson)
            {
                _template = SeriesTemplate.FromJson(TemplateJson);
                _templateFor = TemplateJson;
            }

            return _template ?? SeriesTemplate.Empty;
        }
    }

    private RecurrenceRule? _rule;
    private string? _ruleFor;
    private SeriesTemplate? _template;
    private string? _templateFor;

    public void SetRule(RecurrenceRule rule, Hlc stamp)
    {
        ArgumentNullException.ThrowIfNull(rule);

        RuleJson = rule.ToJson();
        _ruleFor = RuleJson;
        _rule = rule;
        Touch(stamp);
    }

    public void SetTemplate(SeriesTemplate template, Hlc stamp)
    {
        ArgumentNullException.ThrowIfNull(template);

        TemplateJson = template.ToJson();
        _templateFor = TemplateJson;
        _template = template;
        Touch(stamp);
    }

    /// <summary>
    /// Kalendarz, w którym seria stoi jako <b>jedno wydarzenie cykliczne</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wskazanie siedzi na serii, a nie na jej dniach, i to jest cała różnica wobec
    /// sześćdziesięciu osobnych wydarzeń. Osobne znaczyłyby sześćdziesiąt wypraw po sieci
    /// na każdą serię, sześćdziesiąt poprawek przy zmianie nazwy — i dwa wydarzenia na
    /// ten sam dzień, bo pulpit i telefon stawiają okno niezależnie, a dopóki wskazanie
    /// nie dojedzie synchronizacją, oba utworzyłyby własne. Przy jednym wydarzeniu na
    /// serię pytanie „które urządzenie wysyła" znika: wysyłają to samo.
    /// </para>
    /// <para>
    /// Pole scalane jak każde inne, więc po synchronizacji oba urządzenia czytają w nim
    /// to samo i drugie przestaje mieć co tworzyć.
    /// </para>
    /// </remarks>
    public Guid? SharedCalendarId { get; private set; }

    /// <summary>Identyfikator wydarzenia cyklicznego u źródła.</summary>
    public string? SharedEventId { get; private set; }

    /// <summary>Zapamiętanie, gdzie ta seria stoi jako wydarzenie.</summary>
    public void Share(Guid calendarId, string eventId, Hlc stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        SharedCalendarId = calendarId;
        SharedEventId = eventId;
        Touch(stamp);
    }

    /// <summary>Zapomnienie o powiązaniu. Samo wydarzenie kasuje warstwa wyżej.</summary>
    public void Unshare(Hlc stamp)
    {
        SharedCalendarId = null;
        SharedEventId = null;
        Touch(stamp);
    }

    public void MoveStart(DateOnly starts, Hlc stamp)
    {
        Starts = starts;
        Touch(stamp);
    }

    /// <summary>
    /// Koniec serii na danym dniu: ten zostaje, dalszych nie ma.
    /// </summary>
    /// <remarks>
    /// Data końca w regule, a nie skasowanie serii: wystąpienia minione zostają tym,
    /// czym są, a dopełnianie okna przestaje mieć co dokładać. Skasowanie serii musiałoby
    /// dodatkowo odpowiedzieć na pytanie, co z historią — a ona ma zostać.
    /// </remarks>
    public void EndOn(DateOnly last, Hlc stamp) =>
        SetRule(Rule.Until is { } until && until <= last ? Rule : Rule.EndingOn(last), stamp);
}
