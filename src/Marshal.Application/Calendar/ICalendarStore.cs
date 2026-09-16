using Marshal.Domain.Calendar;

namespace Marshal.Application.Calendar;

/// <summary>Zapis i odczyt kopii kalendarzy. Lokalny, poza dziennikiem zmian.</summary>
public interface ICalendarStore
{
    Task<IReadOnlyList<CalendarSource>> SourcesAsync(CancellationToken ct = default);

    Task<CalendarCursor?> CursorAsync(Guid sourceId, CancellationToken ct = default);

    void SaveCursor(Guid sourceId, string? syncToken, DateTimeOffset fetchedAt);

    /// <summary>Wydarzenia widocznych kalendarzy w zakresie dni.</summary>
    Task<IReadOnlyList<CalendarEvent>> EventsAsync(
        DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default);

    /// <summary>Wstawia albo aktualizuje po parze źródło–identyfikator zewnętrzny.</summary>
    Task UpsertAsync(Guid sourceId, IReadOnlyList<FeedEvent> events, CancellationToken ct = default);

    /// <summary>
    /// Po odczycie pełnym: oznacza jako odwołane wszystko, czego w nim nie było.
    /// </summary>
    /// <remarks>
    /// Tylko po pełnym. Przy przyrostowym „nie przyszło" znaczy „bez zmian", więc to samo
    /// sprzątanie skasowałoby cały kalendarz przy pierwszym odczycie, w którym nic się
    /// nie zmieniło.
    /// </remarks>
    Task<int> MarkMissingCancelledAsync(
        Guid sourceId, IReadOnlyList<string> seen, CancellationToken ct = default);

    void AddSource(CalendarSource source);

    Task SaveChangesAsync(CancellationToken ct = default);
}
