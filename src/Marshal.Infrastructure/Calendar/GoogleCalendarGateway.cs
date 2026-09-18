using System.Globalization;
using Google;
using System.Net;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Calendar;
using Marshal.Infrastructure.Sync.Google;

namespace Marshal.Infrastructure.Calendar;

/// <summary>
/// Kanał kalendarza Google podłączony przy składaniu zależności, logujący się dopiero
/// przy pierwszym pobraniu.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GoogleCalendarFeed"/> wymaga gotowej usługi Google, a ta powstaje dopiero
/// po zalogowaniu — czyli po tym, jak kontener już wszystko złożył. Bez tego pośrednika
/// kanału Google **nie było w ogóle wśród zarejestrowanych kanałów**: odświeżanie
/// kalendarzy przechodziło po źródłach, nie znajdowało kanału dla rodzaju Google
/// i po cichu je pomijało. Włączenie API w konsoli niczego nie zmieniało, bo aplikacja
/// nigdy nie zadawała pytania.
/// </para>
/// <para>
/// Bez poświadczeń albo bez zgody na kalendarz **mówi dlaczego**, zamiast oddawać pusty
/// wynik. Pusty wynik był tu błędem w projekcie: niepodłączone konto wyglądało wtedy
/// dokładnie tak samo jak kalendarz bez wydarzeń — „odświeżone 1, wydarzeń 0" — czyli
/// jedyna informacja, która mogła pomóc, ginęła w drodze do ekranu.
/// </para>
/// </remarks>
/// <summary>Kalendarz z konta, do wyboru na ekranie.</summary>
public sealed record GoogleCalendarInfo(string Id, string Name, string? Color);

public sealed class GoogleCalendarGateway(ISettings settings, string databasePath)
    : ICalendarFeed, ICalendarWriter
{
    private GoogleCalendarFeed? _kanal;

    private CalendarService? _usluga;

    public CalendarKind Kind => CalendarKind.Google;

    public async Task<FeedResult> FetchAsync(
        CalendarSource source, string? syncToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            return await (await PolaczAsync(ct)).FetchAsync(source, syncToken, ct);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Google odpowiada 404 na **nazwę** kalendarza, bo chce identyfikatora:
            // „primary" albo czegoś w rodzaju abc@group.calendar.google.com. Gołe
            // „NotFound" nie mówi tego wcale, a to najczęstsza pomyłka przy ręcznym
            // wpisywaniu — stąd przycisk pobierający listę z konta.
            throw new InvalidOperationException(
                $"Google nie zna kalendarza „{source.ExternalId}”. To wygląda na nazwę, "
                + "a potrzebny jest identyfikator — użyj przycisku „Pobierz moje kalendarze”.");
        }
    }

    /// <summary>
    /// Kalendarze widoczne na koncie.
    /// </summary>
    /// <remarks>
    /// Istnieje po to, żeby nikt nie musiał przepisywać identyfikatorów z ustawień
    /// Google. Pierwsza wersja ekranu kazała je wpisywać ręcznie i skończyło się
    /// pięcioma kalendarzami dodanymi po nazwie — czyli pięcioma błędami 404 z rzędu.
    /// </remarks>
    public async Task<IReadOnlyList<GoogleCalendarInfo>> ListAsync(CancellationToken ct = default)
    {
        await PolaczAsync(ct);

        var odpowiedz = await _usluga!.CalendarList.List().ExecuteAsync(ct);

        return (odpowiedz.Items ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k.Id))
            .Select(k => new GoogleCalendarInfo(
                k.Id,
                string.IsNullOrWhiteSpace(k.Summary) ? k.Id : k.Summary,

                // Barwa prosto z konta: kalendarze rozpoznaje się po kolorze, który
                // się w Google ustawiło, a nie po kolorze, który wylosuje aplikacja.
                k.BackgroundColor))
            .OrderBy(k => k.Name, StringComparer.CurrentCulture)
            .ToArray();
    }

    public async Task<string> CreateAsync(
        CalendarSource source, CalendarDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        await PolaczAsync(ct);

        var utworzone = await _usluga!.Events
            .Insert(Zbuduj(draft), source.ExternalId)
            .ExecuteAsync(ct);

        return utworzone.Id
            ?? throw new InvalidOperationException(
                "Google przyjął wydarzenie, ale nie oddał jego identyfikatora. "
                + "Odśwież kalendarz, żeby je zobaczyć.");
    }

    /// <summary>
    /// Zmiana wydarzenia.
    /// </summary>
    /// <remarks>
    /// <b>Patch, nie Update.</b> Update zastępuje całe wydarzenie tym, co wyślemy —
    /// a my znamy tylko tytuł, godziny i miejsce. Uczestnicy, przypomnienia, opis,
    /// załączniki i powtarzalność zostałyby wtedy wyczyszczone przez samo poprawienie
    /// literówki w tytule. Patch dotyka wyłącznie pól, które podajemy.
    /// </remarks>
    public async Task UpdateAsync(
        CalendarSource source, string externalId, CalendarDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        await PolaczAsync(ct);

        await _usluga!.Events
            .Patch(Zbuduj(draft), source.ExternalId, Podstawowe(externalId))
            .ExecuteAsync(ct);
    }

    /// <summary>Zmiana samej nazwy — jedno pole w Patchu, więc godzin nie ma czym ruszyć.</summary>
    public async Task RenameAsync(
        CalendarSource source, string externalId, string title, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        await PolaczAsync(ct);

        await _usluga!.Events
            .Patch(new Event { Summary = title }, source.ExternalId, Podstawowe(externalId))
            .ExecuteAsync(ct);
    }

    public async Task DeleteAsync(
        CalendarSource source, string externalId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        await PolaczAsync(ct);

        await _usluga!.Events.Delete(source.ExternalId, Podstawowe(externalId)).ExecuteAsync(ct);
    }

    /// <summary>
    /// Identyfikator wydarzenia zrozumiały dla Google.
    /// </summary>
    /// <remarks>
    /// Wystąpienia serii przychodzą z API jako <c>identyfikator_20260917T140000Z</c>.
    /// Zmiana takiego wystąpienia jest zmianą jego samego, nie całej serii — i tak ma
    /// zostać. Ale nasz własny klucz z kanału iCal ma po kresce datę wystąpienia,
    /// a tego Google nie zna; tu przychodzą wyłącznie identyfikatory Google, więc
    /// oddajemy je bez zmian i pilnujemy tylko, żeby nie przemycić naszej kreski.
    /// </remarks>
    private static string Podstawowe(string externalId) =>
        externalId.Split('|', 2)[0];

    private static Event Zbuduj(CalendarDraft draft) => new()
    {
        Summary = draft.Title,
        Location = draft.Location,
        Start = Kiedy(draft.Start, draft.AllDay),
        End = Kiedy(draft.End, draft.AllDay),
    };

    /// <summary>
    /// Chwila albo data — Google trzyma to w dwóch różnych polach.
    /// </summary>
    /// <remarks>
    /// Całodniowe idzie jako sama data, bez strefy, bo data strefy nie ma. Wysłane
    /// jako chwila o północy czasu lokalnego wypadałoby u kogoś na wschód dzień
    /// wcześniej — a „wzięte na dziś" ma znaczyć dziś u każdego, kto to widzi.
    /// </remarks>
    private static EventDateTime Kiedy(DateTimeOffset chwila, bool calodniowe) =>
        calodniowe
            ? new EventDateTime { Date = chwila.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : new EventDateTime { DateTimeDateTimeOffset = chwila };

    private async Task<GoogleCalendarFeed> PolaczAsync(CancellationToken ct)
    {
        if (_kanal is not null)
        {
            return _kanal;
        }

        if (string.IsNullOrWhiteSpace(settings.GoogleClientId)
            || string.IsNullOrWhiteSpace(settings.GoogleClientSecret))
        {
            throw new InvalidOperationException(
                "nie ma poświadczeń Google — wpisz je w Ustawieniach, sekcja Konto Google.");
        }

        if (!settings.GoogleCalendarEnabled)
        {
            throw new InvalidOperationException(
                "zgoda obejmuje tylko Dysk. Zaznacz „Czytaj i zmieniaj mój kalendarz Google” "
                + "i kliknij „Zapisz i zsynchronizuj”, żeby poprosić o dostęp do kalendarza.");
        }

        var poswiadczenie = await GoogleDriveFactory.AuthorizeAsync(
            settings.GoogleClientId!,
            settings.GoogleClientSecret!,
            Path.Combine(Path.GetDirectoryName(databasePath)!, "google"),
            withCalendar: true,
            ct);

        _usluga = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = poswiadczenie,
            ApplicationName = "Marshal",
        });

        return _kanal = new GoogleCalendarFeed(_usluga);
    }
}
