using System.Globalization;
using System.Net;
using Google;
using Google.Apis.Calendar.v3;
using Marshal.Application.Calendar;
using Marshal.Domain.Calendar;
using GoogleCalendar = Google.Apis.Calendar.v3.CalendarService;

namespace Marshal.Infrastructure.Calendar;

/// <summary>
/// Odczyt kalendarza Google (spec 10.1).
/// </summary>
/// <remarks>
/// <para>
/// Przyrostowo przez żeton, pełne odświeżenie gdy żeton wygaśnie. Google zwraca wtedy
/// 410 i to jest normalna droga, nie awaria: żetony mają termin ważności, a kalendarz
/// nietknięty przez tydzień prawie na pewno ten termin przekroczy.
/// </para>
/// <para>
/// <b>Tylko odczyt — ta klasa.</b> Zapis idzie osobną drogą, przez pisarza kalendarza,
/// i jest obudowany własnymi zasadami: łatamy zamiast nadpisywać, najpierw źródło,
/// kasujemy tylko to, co wskazano wprost. Rozdzielone, bo to jedyne miejsce w całym
/// projekcie, gdzie awaria niszczy dane poza aplikacją — i czytanie nie ma dzielić
/// z pisaniem ani jednej linii, którą dałoby się pomylić.
/// </para>
/// </remarks>
public sealed class GoogleCalendarFeed(GoogleCalendar service) : ICalendarFeed
{
    /// <summary>Ile miesięcy wstecz i w przód czytać przy odczycie pełnym.</summary>
    private const int WindowMonths = 6;

    public CalendarKind Kind => CalendarKind.Google;

    public async Task<FeedResult> FetchAsync(
        CalendarSource source, string? syncToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            return await ReadAsync(source, syncToken, ct);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Gone)
        {
            // Żeton przeterminowany. Czytamy od nowa — i to jest jedyna droga, bo Google
            // nie umie powiedzieć, co się zmieniło od chwili, której już nie pamięta.
            return await ReadAsync(source, syncToken: null, ct);
        }
    }

    private async Task<FeedResult> ReadAsync(
        CalendarSource source, string? syncToken, CancellationToken ct)
    {
        var wydarzenia = new List<FeedEvent>();
        string? strona = null;
        string? nowyZeton = null;
        var pelny = syncToken is null;

        do
        {
            var zapytanie = service.Events.List(source.ExternalId);
            zapytanie.MaxResults = 250;
            zapytanie.PageToken = strona;

            // Wystąpienia serii zamiast reguł: siatka godzinowa potrzebuje konkretnych
            // godzin, a rozwijaniem powtórzeń Google zajmuje się lepiej niż my.
            zapytanie.SingleEvents = true;

            // Odwołane też, bo przy odczycie przyrostowym to jedyny sposób, żeby się
            // dowiedzieć, że coś zniknęło.
            zapytanie.ShowDeleted = true;

            if (syncToken is null)
            {
                zapytanie.TimeMinDateTimeOffset = DateTimeOffset.UtcNow.AddMonths(-WindowMonths);
                zapytanie.TimeMaxDateTimeOffset = DateTimeOffset.UtcNow.AddMonths(WindowMonths);
            }
            else
            {
                // Zakresu i żetonu nie wolno podać razem — Google odrzuca takie zapytanie.
                zapytanie.SyncToken = syncToken;
            }

            var odpowiedz = await zapytanie.ExecuteAsync(ct);

            foreach (var wydarzenie in odpowiedz.Items ?? [])
            {
                if (Convert(wydarzenie) is { } przetworzone)
                {
                    wydarzenia.Add(przetworzone);
                }
            }

            strona = odpowiedz.NextPageToken;
            nowyZeton = odpowiedz.NextSyncToken ?? nowyZeton;
        }
        while (!string.IsNullOrEmpty(strona));

        var wpis = await WpisAsync(source, ct);

        return new FeedResult(wydarzenia, nowyZeton, pelny, wpis.Barwa, wpis.TylkoOdczyt);
    }

    /// <summary>
    /// Barwa kalendarza i poziom dostępu — obie rzeczy z konta Google.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Żadnej z nich nie ma w odpowiedzi z wydarzeniami — siedzą na liście kalendarzy
    /// konta, stąd osobne zapytanie. Jedno na kalendarz na odświeżenie, a odświeżenie
    /// jest ręczne albo co pięć minut.
    /// </para>
    /// <para>
    /// Poziom dostępu przy każdym pobraniu, a nie raz przy podłączaniu: dostęp się
    /// zmienia. Ktoś dopuszcza do swojego kalendarza i wtedy zapis zaczyna być możliwy,
    /// albo odbiera dostęp i wtedy przestaje — a aplikacja, która pyta o to raz, myli
    /// się od tamtej chwili do końca.
    /// </para>
    /// <para>
    /// Kalendarza, którego nie ma na liście konta (na przykład publicznego, dodanego
    /// po samym identyfikatorze), da się czytać, ale ani barwy, ani poziomu dostępu
    /// dla niego nie ma — i to jest jedyny przypadek, w którym 404 nie jest tu błędem.
    /// Nie połykamy przez to niczego innego: wszystkie pozostałe niepowodzenia idą
    /// dalej i lądują w raporcie.
    /// </para>
    /// </remarks>
    private async Task<(string? Barwa, bool? TylkoOdczyt)> WpisAsync(
        CalendarSource source, CancellationToken ct)
    {
        try
        {
            var wpis = await service.CalendarList.Get(source.ExternalId).ExecuteAsync(ct);

            return (wpis.BackgroundColor, TylkoOdczyt(wpis.AccessRole));
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Poziom dostępu Google na odpowiedź „czy wolno tu pisać".
    /// </summary>
    /// <remarks>
    /// Google nazywa cztery: <c>owner</c>, <c>writer</c>, <c>reader</c>
    /// i <c>freeBusyReader</c>. Pisać wolno dwóm pierwszym.
    ///
    /// Nierozpoznane znaczy „wolno" i to jest rozstrzygnięcie: nowa nazwa poziomu,
    /// której jeszcze nie znamy, zablokowałaby zapis do kalendarza, do którego wolno,
    /// a objawem byłoby pole, którego nie da się kliknąć, bez żadnego wyjaśnienia.
    /// Odwrotna pomyłka kończy się odmową od Google — czyli zdaniem na ekranie.
    /// </remarks>
    private static bool? TylkoOdczyt(string? poziom) => poziom is { Length: > 0 }
        ? !string.Equals(poziom, "owner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(poziom, "writer", StringComparison.OrdinalIgnoreCase)
        : null;

    private static FeedEvent? Convert(Google.Apis.Calendar.v3.Data.Event wydarzenie)
    {
        if (string.IsNullOrEmpty(wydarzenie.Id))
        {
            return null;
        }

        var odwolane = string.Equals(wydarzenie.Status, "cancelled", StringComparison.Ordinal);

        var start = Moment(wydarzenie.Start);
        var koniec = Moment(wydarzenie.End) ?? start?.AddHours(1);

        // Odwołane wydarzenie przy odczycie przyrostowym przychodzi często jako sam
        // identyfikator, bez dat. Zostaje nagrobkiem z datami zastępczymi — i tak liczy
        // się dla niego wyłącznie to, że jest odwołane.
        if (start is null || koniec is null)
        {
            return odwolane
                ? new FeedEvent(
                    wydarzenie.Id, "(odwołane)", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                    false, null, Cancelled: true)
                : null;
        }

        return new FeedEvent(
            wydarzenie.Id,
            string.IsNullOrWhiteSpace(wydarzenie.Summary) ? "(bez tytułu)" : wydarzenie.Summary,
            start.Value,
            koniec.Value,
            IsAllDay: wydarzenie.Start?.DateTimeDateTimeOffset is null,
            wydarzenie.Location,
            odwolane);
    }

    private static DateTimeOffset? Moment(Google.Apis.Calendar.v3.Data.EventDateTime? when) =>
        when?.DateTimeDateTimeOffset ?? ParseDate(when?.Date);

    /// <summary>
    /// Data wydarzenia całodniowego.
    /// </summary>
    /// <remarks>
    /// Przyjmuje <see cref="object"/>, bo pole daty w wygenerowanym kliencie bywa raz
    /// tekstem, raz datą, zależnie od wersji pakietu — a nie da się tego sprawdzić bez
    /// kompilatora pod ręką. Obsłużenie obu postaci jest tańsze niż przebieg CI na każde
    /// zgadnięcie i nie kosztuje nic poza tym komentarzem.
    /// </remarks>
    private static DateTimeOffset? ParseDate(object? date) => date switch
    {
        string tekst when DateOnly.TryParse(tekst, CultureInfo.InvariantCulture, out var dzien) =>
            new DateTimeOffset(dzien.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        DateTime chwila => new DateTimeOffset(chwila.Date, TimeSpan.Zero),
        _ => null,
    };
}
