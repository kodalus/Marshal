using Marshal.Domain.Calendar;

namespace Marshal.Application.Calendar;

/// <summary>Zapis i odczyt kopii kalendarzy. Lokalny, poza dziennikiem zmian.</summary>
public interface ICalendarStore
{
    Task<IReadOnlyList<CalendarSource>> SourcesAsync(CancellationToken ct = default);

    /// <summary>
    /// Wszystkie podłączenia, także odrzucone.
    /// </summary>
    /// <remarks>
    /// Do jednej rzeczy: rozstrzygnięcia, czym <b>było</b> podłączenie, na które coś
    /// jeszcze wskazuje. Odrzucenie jest nagrobkiem, więc wiersz zostaje i wciąż niesie
    /// rodzaj oraz identyfikator zewnętrzny — a to wystarczy, żeby znaleźć żyjącego
    /// bliźniaka i przepiąć wskazanie zamiast odmówić zapisu.
    /// </remarks>
    Task<IReadOnlyList<CalendarSource>> AllSourcesAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Ile wydarzeń leży w bazie w ogóle, bez zakresu i bez odwołanych.
    /// </summary>
    /// <remarks>
    /// Do jednego pytania: czy pusta siatka znaczy „nic nie pobrano", czy „pobrano,
    /// ale nie na te dni". Bez tej liczby jedno od drugiego nie różni się niczym,
    /// co widać na ekranie.
    /// </remarks>
    Task<int> CountAsync(CancellationToken ct = default);

    void AddSource(CalendarSource source);

    /// <summary>
    /// Kasuje kopię wydarzeń i kursor odrzuconego źródła.
    /// </summary>
    /// <remarks>
    /// Kasowanie, a nie nagrobek: wydarzenia są lokalną kopią cudzych danych i nie
    /// podlegają synchronizacji, więc nie ma komu opowiadać, że zniknęły. Zostawione
    /// leżałyby w bazie na zawsze — niewidoczne, bo źródło jest odrzucone, i policzone
    /// w „ile w bazie", czyli mylące dokładnie tam, gdzie się patrzy przy szukaniu
    /// duplikatów.
    /// </remarks>
    Task<int> ForgetEventsAsync(Guid sourceId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
