using Marshal.Application.Calendar;
using Marshal.Domain.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Marshal.Infrastructure.Calendar;

/// <summary>
/// Kanał iCal pobierany po adresie (spec 10.1).
/// </summary>
/// <remarks>
/// <para>
/// Zawsze czyta całość — plik iCal nie ma pojęcia odczytu przyrostowego. Przy kanale
/// odświeżanym co godzinę to kilkadziesiąt kilobajtów i nie ma czego optymalizować.
/// </para>
/// <para>
/// <b>Nie sprawdzone na żywym kanale.</b> Rozbiór treści ma testy na pliku wpisanym
/// wprost; nieprzetestowane zostaje samo pobranie po sieci.
/// </para>
/// </remarks>
public sealed class IcalFeed(HttpClient http) : ICalendarFeed
{
    /// <summary>Ile miesięcy wstecz i w przód rozwijać powtórzenia.</summary>
    /// <remarks>
    /// Kanał z cotygodniowym wydarzeniem bez daty końca rozwinąłby się w nieskończoność.
    /// Okno jest szersze niż to, co pokazuje siatka, żeby przewinięcie o tydzień nie
    /// wymagało pobierania od nowa.
    /// </remarks>
    private const int WindowMonths = 6;

    public CalendarKind Kind => CalendarKind.Ical;

    public async Task<FeedResult> FetchAsync(
        CalendarSource source, string? syncToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var content = await http.GetStringAsync(source.ExternalId, ct);

        return new FeedResult(
            Parse(content, DateTime.UtcNow), SyncToken: null, IsFull: true, ParseColor(content));
    }

    /// <summary>
    /// Barwa kanału, jeśli ją podaje.
    /// </summary>
    /// <remarks>
    /// <c>X-APPLE-CALENDAR-COLOR</c> nie jest w normie iCal, ale wystawia je wszystko,
    /// co w ogóle podaje kolor — biblioteka do rozbioru nie wpuszcza własnych pól
    /// zaczynających się od X, więc szukamy w tekście. Wartość to zwykle <c>#RRGGBB</c>
    /// albo <c>#RRGGBBAA</c>; ósemki nie skracamy, bo alfę i tak nakłada siatka.
    /// </remarks>
    public static string? ParseColor(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var cleaned = line.Trim();

            if (!cleaned.StartsWith("X-APPLE-CALENDAR-COLOR", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colon = cleaned.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0)
            {
                continue;
            }

            var color = cleaned[(colon + 1)..].Trim();

            return string.IsNullOrEmpty(color) ? null : color;
        }

        return null;
    }

    /// <summary>
    /// Rozbiór treści iCal. Wydzielony z pobierania, żeby dało się sprawdzić bez sieci —
    /// bo to tutaj siedzą wszystkie pułapki formatu.
    /// </summary>
    public static IReadOnlyList<FeedEvent> Parse(string content, DateTime now)
    {
        var calendarId = Ical.Net.Calendar.Load(content);
        var result = new List<FeedEvent>();

        var occurrences = calendarId.GetOccurrences(
            now.AddMonths(-WindowMonths), now.AddMonths(WindowMonths));

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Source is not IcalEvent ev)
            {
                continue;
            }

            var start = occurrence.Period.StartTime.AsDateTimeOffset;
            var end = occurrence.Period.EndTime?.AsDateTimeOffset ?? start.AddHours(1);

            result.Add(new FeedEvent(
                // Identyfikator musi rozróżniać wystąpienia serii: wszystkie mają ten sam
                // UID, więc bez daty w kluczu cotygodniowe spotkanie zapisałoby się raz.
                $"{ev.Uid}|{start:yyyy-MM-ddTHH:mm:ssK}",
                string.IsNullOrWhiteSpace(ev.Summary) ? "(bez tytułu)" : ev.Summary,
                start,
                end,
                ev.IsAllDay,
                ev.Location,

                // Odwołanie w kanale iCal nie przychodzi jako zdarzenie, tylko jako brak
                // wydarzenia w kolejnym pobraniu. Sprzątaniem zajmuje się usługa, która
                // wie, że odczyt był pełny.
                Cancelled: false));
        }

        return result;
    }
}
