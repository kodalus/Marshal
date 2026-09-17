using Marshal.Domain.Calendar;

namespace Marshal.Application.Calendar;

/// <summary>Jedno wydarzenie w postaci, w jakiej przyszło ze źródła.</summary>
public sealed record FeedEvent(
    string ExternalId,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool IsAllDay,
    string? Location,
    bool Cancelled);

/// <summary>
/// Wynik odczytu. <see cref="SyncToken"/> puste znaczy, że następny odczyt musi być pełny.
/// </summary>
/// <remarks>
/// <see cref="IsFull"/> odróżnia dociągnięcie zmian od pełnego przeczytania kalendarza.
/// Bez tego rozróżnienia nie da się posprzątać wydarzeń, które zniknęły: przy odczycie
/// przyrostowym „nie przyszło" znaczy „bez zmian", a przy pełnym — „już go nie ma".
/// </remarks>
/// <param name="Color">
/// Barwa kalendarza u źródła, jeśli ją podaje. Puste znaczy „nie wiem", a nie
/// „bez koloru" — dlatego nie kasuje barwy już zapisanej. Jedenaście kalendarzy
/// podłączonych przed wprowadzeniem barw ma w bazie pusto i inaczej zostałoby
/// szare na zawsze: droga przez odłączenie i podłączenie od nowa to nie jest
/// coś, czego można wymagać od kogokolwiek.
/// </param>
public sealed record FeedResult(
    IReadOnlyList<FeedEvent> Events, string? SyncToken, bool IsFull, string? Color = null);

/// <summary>
/// Odczyt kalendarza zewnętrznego (spec 10.1). Tylko odczyt — zapis jest świadomie
/// odłożony (10.2), bo błąd w dwustronnej synchronizacji potrafi skasować wydarzenia
/// w prawdziwym kalendarzu.
/// </summary>
public interface ICalendarFeed
{
    CalendarKind Kind { get; }

    /// <param name="syncToken">
    /// Żeton z poprzedniego odczytu albo <c>null</c> na odczyt pełny. Kanały iCal żetonu
    /// nie mają i zawsze czytają się w całości — nie jest to gorsze rozwiązanie, tylko
    /// inne: plik iCal jest z natury całością.
    /// </param>
    Task<FeedResult> FetchAsync(
        CalendarSource source, string? syncToken, CancellationToken ct = default);
}
