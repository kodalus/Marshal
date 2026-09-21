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

        // Dwa opakowania, bo to dwie różne rzeczy. Wewnętrzne rozpoznaje przeterminowany
        // żeton i czyta od nowa; zewnętrzne tłumaczy **każdą** pozostałą awarię na zdanie,
        // bo ten tekst idzie na ekran pod kalendarzem, a nie tylko do dziennika.
        try
        {
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
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException(FeedTrouble.Say(e), e);
        }
    }

    private async Task<FeedResult> ReadAsync(
        CalendarSource source, string? syncToken, CancellationToken ct)
    {
        var events = new List<FeedEvent>();
        string? page = null;
        string? newToken = null;
        var full = syncToken is null;

        do
        {
            var query = service.Events.List(source.ExternalId);
            query.MaxResults = 250;
            query.PageToken = page;

            // Wystąpienia serii zamiast reguł: siatka godzinowa potrzebuje konkretnych
            // godzin, a rozwijaniem powtórzeń Google zajmuje się lepiej niż my.
            query.SingleEvents = true;

            // Odwołane też, bo przy odczycie przyrostowym to jedyny sposób, żeby się
            // dowiedzieć, że coś zniknęło.
            query.ShowDeleted = true;

            if (syncToken is null)
            {
                query.TimeMinDateTimeOffset = DateTimeOffset.UtcNow.AddMonths(-WindowMonths);
                query.TimeMaxDateTimeOffset = DateTimeOffset.UtcNow.AddMonths(WindowMonths);
            }
            else
            {
                // Zakresu i żetonu nie wolno podać razem — Google odrzuca takie zapytanie.
                query.SyncToken = syncToken;
            }

            var response = await query.ExecuteAsync(ct);

            foreach (var ev in response.Items ?? [])
            {
                if (Convert(ev) is { } processed)
                {
                    events.Add(processed);
                }
            }

            page = response.NextPageToken;
            newToken = response.NextSyncToken ?? newToken;
        }
        while (!string.IsNullOrEmpty(page));

        var entry = await EntryAsync(source, ct);

        return new FeedResult(events, newToken, full, entry.Color, entry.ReadOnly);
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
    private async Task<(string? Color, bool? ReadOnly)> EntryAsync(
        CalendarSource source, CancellationToken ct)
    {
        try
        {
            var entry = await service.CalendarList.Get(source.ExternalId).ExecuteAsync(ct);

            return (entry.BackgroundColor, ReadOnly(entry.AccessRole));
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
    private static bool? ReadOnly(string? level) => level is { Length: > 0 }
        ? !string.Equals(level, "owner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(level, "writer", StringComparison.OrdinalIgnoreCase)
        : null;

    private static FeedEvent? Convert(Google.Apis.Calendar.v3.Data.Event ev)
    {
        if (string.IsNullOrEmpty(ev.Id))
        {
            return null;
        }

        var cancelled = string.Equals(ev.Status, "cancelled", StringComparison.Ordinal);

        var start = Moment(ev.Start);
        var end = Moment(ev.End) ?? start?.AddHours(1);

        // Odwołane wydarzenie przy odczycie przyrostowym przychodzi często jako sam
        // identyfikator, bez dat. Zostaje nagrobkiem z datami zastępczymi — i tak liczy
        // się dla niego wyłącznie to, że jest odwołane.
        if (start is null || end is null)
        {
            return cancelled
                ? new FeedEvent(
                    ev.Id, "(odwołane)", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                    false, null, Cancelled: true)
                : null;
        }

        return new FeedEvent(
            ev.Id,
            string.IsNullOrWhiteSpace(ev.Summary) ? "(bez tytułu)" : ev.Summary,
            start.Value,
            end.Value,
            IsAllDay: ev.Start?.DateTimeDateTimeOffset is null,
            ev.Location,
            cancelled);
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
        string text when DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var day) =>
            new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        DateTime moment => new DateTimeOffset(moment.Date, TimeSpan.Zero),
        _ => null,
    };
}
