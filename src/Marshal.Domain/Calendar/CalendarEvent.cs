namespace Marshal.Domain.Calendar;

/// <summary>
/// Wydarzenie pobrane z kalendarza zewnętrznego.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lokalne, niesynchronizowane</b> — i to jest rozstrzygnięcie, nie oszczędność.
/// To jest kopia cudzych danych: oba urządzenia sięgają po nią do tego samego konta,
/// więc rozsyłanie jej między nimi podwajałoby ruch i stawiałoby pytania o scalanie
/// czegoś, czego nie jesteśmy właścicielem. Każde urządzenie pobiera sobie samo.
/// </para>
/// <para>
/// Klucz jest złożony z pary źródło–identyfikator zewnętrzny, bo taki jest naturalny
/// klucz po drugiej stronie. Własny identyfikator byłby trzecim numerem tego samego
/// wydarzenia i przy każdym odświeżeniu trzeba by go szukać po parze i tak.
/// </para>
/// </remarks>
public sealed class CalendarEvent
{
    private CalendarEvent()
    {
        ExternalId = string.Empty;
        Title = string.Empty;
    }

    public CalendarEvent(
        Guid sourceId,
        string externalId,
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        bool isAllDay,
        string? location = null)
    {
        SourceId = sourceId;
        ExternalId = externalId;
        Title = title;
        StartsAt = startsAt;
        EndsAt = endsAt;
        IsAllDay = isAllDay;
        Location = location;
    }

    public Guid SourceId { get; private set; }

    public string ExternalId { get; private set; }

    public string Title { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public bool IsAllDay { get; private set; }

    public string? Location { get; private set; }

    /// <summary>
    /// Odwołane zostaje w bazie jako nagrobek, tak jak wszystko inne.
    /// </summary>
    /// <remarks>
    /// Przy odczycie przyrostowym Google przysyła odwołanie jako zmianę wydarzenia,
    /// a nie jako jego brak. Fizyczne usunięcie przy odświeżeniu znaczyłoby, że kolejny
    /// odczyt przyrostowy nie ma czego zaktualizować i odwołane wydarzenie wraca.
    /// </remarks>
    public bool Cancelled { get; private set; }

    public void Update(
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        bool isAllDay,
        string? location,
        bool cancelled)
    {
        Title = title;
        StartsAt = startsAt;
        EndsAt = endsAt;
        IsAllDay = isAllDay;
        Location = location;
        Cancelled = cancelled;
    }
}

/// <summary>
/// Dokąd doczytaliśmy dany kalendarz. Lokalne — żeton odczytu przyrostowego jest
/// własnością urządzenia, które go dostało.
/// </summary>
public sealed class CalendarCursor
{
    private CalendarCursor()
    {
    }

    public CalendarCursor(Guid sourceId, string? syncToken, DateTimeOffset fetchedAt)
    {
        SourceId = sourceId;
        SyncToken = syncToken;
        FetchedAt = fetchedAt;
    }

    public Guid SourceId { get; private set; }

    /// <summary>Puste znaczy „następny odczyt musi być pełny".</summary>
    public string? SyncToken { get; private set; }

    public DateTimeOffset FetchedAt { get; private set; }

    public void Update(string? syncToken, DateTimeOffset fetchedAt)
    {
        SyncToken = syncToken;
        FetchedAt = fetchedAt;
    }

    /// <summary>
    /// Unieważnienie żetonu po odpowiedzi 410 — następny odczyt idzie od zera (spec 10.1).
    /// </summary>
    public void Invalidate() => SyncToken = null;
}
