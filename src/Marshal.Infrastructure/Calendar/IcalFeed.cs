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

        var tresc = await http.GetStringAsync(source.ExternalId, ct);

        return new FeedResult(
            Parse(tresc, DateTime.UtcNow), SyncToken: null, IsFull: true, ParseColor(tresc));
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
        foreach (var linia in content.Split('\n'))
        {
            var oczyszczona = linia.Trim();

            if (!oczyszczona.StartsWith("X-APPLE-CALENDAR-COLOR", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dwukropek = oczyszczona.IndexOf(':', StringComparison.Ordinal);

            if (dwukropek < 0)
            {
                continue;
            }

            var barwa = oczyszczona[(dwukropek + 1)..].Trim();

            return string.IsNullOrEmpty(barwa) ? null : barwa;
        }

        return null;
    }

    /// <summary>
    /// Rozbiór treści iCal. Wydzielony z pobierania, żeby dało się sprawdzić bez sieci —
    /// bo to tutaj siedzą wszystkie pułapki formatu.
    /// </summary>
    public static IReadOnlyList<FeedEvent> Parse(string content, DateTime now)
    {
        var kalendarz = Ical.Net.Calendar.Load(content);
        var wynik = new List<FeedEvent>();

        var wystapienia = kalendarz.GetOccurrences(
            now.AddMonths(-WindowMonths), now.AddMonths(WindowMonths));

        foreach (var wystapienie in wystapienia)
        {
            if (wystapienie.Source is not IcalEvent wydarzenie)
            {
                continue;
            }

            var start = wystapienie.Period.StartTime.AsDateTimeOffset;
            var koniec = wystapienie.Period.EndTime?.AsDateTimeOffset ?? start.AddHours(1);

            wynik.Add(new FeedEvent(
                // Identyfikator musi rozróżniać wystąpienia serii: wszystkie mają ten sam
                // UID, więc bez daty w kluczu cotygodniowe spotkanie zapisałoby się raz.
                $"{wydarzenie.Uid}|{start:yyyy-MM-ddTHH:mm:ssK}",
                string.IsNullOrWhiteSpace(wydarzenie.Summary) ? "(bez tytułu)" : wydarzenie.Summary,
                start,
                koniec,
                wydarzenie.IsAllDay,
                wydarzenie.Location,

                // Odwołanie w kanale iCal nie przychodzi jako zdarzenie, tylko jako brak
                // wydarzenia w kolejnym pobraniu. Sprzątaniem zajmuje się usługa, która
                // wie, że odczyt był pełny.
                Cancelled: false));
        }

        return wynik;
    }
}
