using Ical.Net.CalendarComponents;
using Marshal.Application.Calendar;
using Marshal.Domain.Calendar;

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
/// <b>Nie sprawdzone na żywym kanale.</b> Rozbiór treści ma testy na wpisanym wprost
/// pliku; nieprzetestowane zostaje samo pobranie po sieci.
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

        var tresc = await http.GetStringAsync(source.ExternalId, ct);
        return new FeedResult(Parse(tresc, DateTime.UtcNow), SyncToken: null, IsFull: true);
    }

    /// <summary>
    /// Rozbiór treści iCal. Wydzielony z pobierania, żeby dało się sprawdzić bez sieci —
    /// bo to tutaj siedzą wszystkie pułapki formatu.
    /// </summary>
    public static IReadOnlyList<FeedEvent> Parse(string content, DateTime now)
    {
        var kalendarz = Ical.Net.Calendar.Load(content);
        var wynik = new List<FeedEvent>();

        foreach (var wystapienie in kalendarz.GetOccurrences(now.AddMonths(-WindowMonths), now.AddMonths(WindowMonths)))
        {
            if (wystapienie.Source is not CalendarEvent wydarzenie)
            {
                continue;
            }

            var start = new DateTimeOffset(wystapienie.Period.StartTime.AsDateTimeOffset.DateTime,
                wystapienie.Period.StartTime.AsDateTimeOffset.Offset);
            var koniec = wystapienie.Period.EndTime is { } k
                ? new DateTimeOffset(k.AsDateTimeOffset.DateTime, k.AsDateTimeOffset.Offset)
                : start.AddHours(1);

            // Wydarzenie całodniowe poznaje się w iCal po dacie bez pory dnia,
            // nie po osobnym polu.
            var calodniowe = !wystapienie.Period.StartTime.HasTime;

            // Identyfikator musi rozróżniać wystąpienia serii: wszystkie mają ten sam
            // UID, więc bez daty w kluczu cotygodniowe spotkanie zapisałoby się raz.
            var id = $"{wydarzenie.Uid}|{start:yyyy-MM-ddTHH:mm:ssK}";

            wynik.Add(new FeedEvent(
                id,
                string.IsNullOrWhiteSpace(wydarzenie.Summary) ? "(bez tytułu)" : wydarzenie.Summary,
                start,
                koniec,
                calodniowe,
                wydarzenie.Location,
                Cancelled: false));
        }

        return wynik;
    }
}
