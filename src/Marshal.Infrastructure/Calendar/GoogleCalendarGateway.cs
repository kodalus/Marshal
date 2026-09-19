using Google.Apis.Auth.OAuth2;
using System.Globalization;
using Google;
using Google.Apis;
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
/// <param name="Account">Konto, z którego pochodzi. Puste znaczy konto główne.</param>
public sealed record GoogleCalendarInfo(
    string Id, string Name, string? Color, string? Account = null,

    /// <summary>Czy do tego kalendarza wolno tylko czytać — z poziomu dostępu Google.</summary>
    bool ReadOnly = false)
{
    /// <summary>Nazwa z kontem, gdy kont jest więcej niż jedno. Na listę wyboru.</summary>
    public string Description
    {
        get
        {
            var name = Account is { Length: > 0 } account ? $"{Name} — {account}" : Name;

            // Dopisek przy nazwie, bo inaczej jedyną drogą do tej wiadomości jest
            // kliknięcie „Obszar" przy wydarzeniu i przeczytanie odmowy.
            return ReadOnly ? $"{name} (tylko do odczytu)" : name;
        }
    }
}

public sealed class GoogleCalendarGateway(ISettings settings, string databasePath)
    : ICalendarFeed, ICalendarWriter
{
    /// <summary>Połączenia trzymane per konto. Pusty klucz to konto główne.</summary>
    /// <remarks>
    /// Każde konto ma własny żeton, więc i własną usługę — jedno połączenie na wszystkie
    /// pokazywałoby kalendarze tego konta, którego żeton akurat wczytano jako pierwszy,
    /// a przy zapisie trafiałoby do cudzego kalendarza z uprawnieniami nie tej osoby.
    /// </remarks>
    private readonly Dictionary<string, CalendarService> _services = [];

    private readonly Dictionary<string, GoogleCalendarFeed> _feeds = [];

    /// <summary>
    /// Brama przed składaniem połączeń.
    /// </summary>
    /// <remarks>
    /// Odświeżanie kalendarzy i odbicie zadania do Google potrafią iść równolegle,
    /// a od kont w miejsce jednego pola są tu słowniki. Jedno pole zapisane z dwóch
    /// wątków naraz kończyło się najwyżej drugim, zbędnym połączeniem; słownik zapisany
    /// z dwóch wątków naraz potrafi zostać uszkodzony w środku — objawem jest odczyt,
    /// który nie wraca. To za wysoka cena za oszczędzenie jednej bramy.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CalendarKind Kind => CalendarKind.Google;

    public async Task<FeedResult> FetchAsync(
        CalendarSource source, string? syncToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            return await (await ConnectAsync(source.Account, ct)).FetchAsync(source, syncToken, ct);
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
    public async Task<IReadOnlyList<GoogleCalendarInfo>> ListAsync(
        string? account = null, CancellationToken ct = default)
    {
        var service = await ServiceAsync(account, ct);
        var response = await service.CalendarList.List().ExecuteAsync(ct);

        return (response.Items ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k.Id))
            .Select(k => new GoogleCalendarInfo(
                k.Id,
                string.IsNullOrWhiteSpace(k.Summary) ? k.Id : k.Summary,

                // Barwa prosto z konta: kalendarze rozpoznaje się po kolorze, który
                // się w Google ustawiło, a nie po kolorze, który wylosuje aplikacja.
                k.BackgroundColor,
                Key(account) is { Length: > 0 } name ? name : null,

                // Poziom dostępu prosto z listy: świąteczne, fazy księżyca i cudze
                // udostępnione bez prawa zmian są tu czytelnikami.
                IsReadOnly(k.AccessRole)))
            .OrderBy(k => k.Name, StringComparer.CurrentCulture)
            .ToArray();
    }

    /// <summary>
    /// Dołożenie konta: zgoda, rozpoznanie adresu i przepisanie żetonu pod ten adres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adresu nie da się poznać przed zgodą, a zgody nie da się poprosić bez klucza
    /// żetonu — stąd klucz tymczasowy na czas jednej wymiany. Adres bierzemy z listy
    /// kalendarzy: kalendarz oznaczony jako główny **ma identyfikator równy adresowi
    /// konta**. Osobne uprawnienie do danych osobowych nie jest więc potrzebne — i nie
    /// prosimy o nie, bo aplikacja, która pyta o więcej, niż jej trzeba, uczy klikania
    /// „zgadzam się" bez czytania.
    /// </para>
    /// <para>
    /// Konto już dodane nie jest błędem: zgoda przechodzi, adres wychodzi ten sam,
    /// żeton nadpisuje poprzedni. Powtórzenie ma być bezpieczne, bo tak wygląda
    /// pierwsza reakcja na „chyba nie zadziałało".
    /// </para>
    /// </remarks>
    public async Task<string> AddAccountAsync(CancellationToken ct = default)
    {
        CheckCredentials();

        var folder = Path.Combine(Path.GetDirectoryName(databasePath)!, "google");
        var temporary = "marshal-konto-nowe";

        var credential = await GoogleDriveFactory.AuthorizeCalendarAsync(
            settings.GoogleClientId!, settings.GoogleClientSecret!, folder, temporary, ct);

        using var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Marshal",
        });

        var list = await service.CalendarList.List().ExecuteAsync(ct);

        var address = (list.Items ?? [])
            .FirstOrDefault(k => k.Primary == true)?.Id;

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException(
                "Zgoda przeszła, ale konto nie podało swojego kalendarza głównego — "
                + "bez niego nie wiadomo, jak je nazwać. Spróbuj jeszcze raz.");
        }

        await GoogleDriveFactory.MoveTokenAsync(
            folder, temporary, GoogleDriveFactory.AccountKey(address), ct);

        // Połączenie złożone na nowo przy pierwszym użyciu — to z klucza tymczasowego
        // już nie ma pod sobą żetonu.
        await _gate.WaitAsync(ct);

        try
        {
            _services.Remove(address);
            _feeds.Remove(address);
        }
        finally
        {
            _gate.Release();
        }

        return address;
    }

    private void CheckCredentials()
    {
        if (string.IsNullOrWhiteSpace(settings.GoogleClientId)
            || string.IsNullOrWhiteSpace(settings.GoogleClientSecret))
        {
            throw new InvalidOperationException(
                "nie ma poświadczeń Google — wpisz je w Ustawieniach, sekcja Konto Google.");
        }
    }

    public async Task<string> CreateAsync(
        CalendarSource source, CalendarDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var service = await ServiceAsync(source.Account, ct);

        Event created;

        try
        {
            created = await service.Events
                .Insert(Build(draft), source.ExternalId)
                .ExecuteAsync(ct);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(ReadOnlyMessage, e);
        }

        return created.Id
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

        var service = await ServiceAsync(source.Account, ct);

        await Gone(() => service.Events
            .Patch(Build(draft), source.ExternalId, Basic(externalId))
            .ExecuteAsync(ct));
    }

    /// <summary>Zmiana samej nazwy — jedno pole w Patchu, więc godzin nie ma czym ruszyć.</summary>
    public async Task RenameAsync(
        CalendarSource source, string externalId, string title, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        var service = await ServiceAsync(source.Account, ct);

        await Gone(() => service.Events
            .Patch(new Event { Summary = title }, source.ExternalId, Basic(externalId))
            .ExecuteAsync(ct));
    }

    public async Task DeleteAsync(
        CalendarSource source, string externalId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        var service = await ServiceAsync(source.Account, ct);

        await Gone(() =>
            service.Events.Delete(source.ExternalId, Basic(externalId)).ExecuteAsync(ct));
    }

    /// <summary>
    /// Dopisanie osoby do gości wydarzenia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cała lista, nie sama dopisywana osoba.</b> Łatanie scala pola, ale nie zagląda
    /// do środka tablicy: goście są jednym polem, którego wartością jest lista. Wysłanie
    /// samej babci nie znaczy „dodaj babcię", tylko „gośćmi są od teraz wyłącznie ci
    /// wymienieni" — a pozostali dostają powiadomienie, że zostali z wydarzenia usunięci.
    /// Stąd odczyt przed zapisem, z Google, bo gości u siebie nie trzymamy w ogóle.
    /// </para>
    /// <para>
    /// <b>Znacznik wersji zamyka szczelinę.</b> Między odczytem a zapisem mieści się
    /// cudza zmiana — ktoś dopisuje kogoś ze swojego telefonu, a my sekundę później
    /// wysyłamy listę sprzed jego zmiany i go zdmuchujemy. Warunek „tylko jeśli
    /// wydarzenie jest nadal w tej wersji" zamienia ciche skasowanie cudzego
    /// zaproszenia w nieudany zapis, o którym da się powiedzieć.
    /// </para>
    /// <para>
    /// Powiadomienia włączone: zaproszenie, o którym nikt się nie dowiaduje, nie jest
    /// zaproszeniem. To jest zresztą różnica między tym a wspólnym kalendarzem —
    /// tam rzeczy pojawiają się po cichu, bo półka jest z góry wspólna.
    /// </para>
    /// </remarks>
    public async Task<bool> InviteAsync(
        CalendarSource source, string externalId, string email, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var service = await ServiceAsync(source.Account, ct);

        var id = Basic(externalId);
        var appended = email.Trim();

        Event? ev = null;

        await Gone(async () =>
            ev = await service.Events.Get(source.ExternalId, id).ExecuteAsync(ct));

        if (ev is null)
        {
            throw new EventGone();
        }

        var guests = ev.Attendees?.ToList() ?? [];

        if (guests.Any(g => string.Equals(g.Email, appended, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        guests.Add(new EventAttendee { Email = appended });

        // Znacznik wersji przepisany z odczytanego wydarzenia — bez niego warunek
        // „tylko jeśli nadal ta wersja" nie ma się do czego odnieść i zapis idzie
        // bezwarunkowo, czyli dokładnie tak, jak być nie miał.
        var patch = service.Events.Patch(
            new Event { Attendees = guests, ETag = ev.ETag },
            source.ExternalId,
            id);

        // Zaproszenie ma dojść — inaczej wydarzenie pojawia się u kogoś bez słowa.
        patch.SendUpdates = EventsResource.PatchRequest.SendUpdatesEnum.All;

        // Zapis wyłącznie na tej wersji, którą przeczytaliśmy. Cudza zmiana w międzyczasie
        // kończy się odmową, a nie skasowaniem jej po cichu.
        patch.ETagAction = ETagAction.IfMatch;

        await Gone(() => patch.ExecuteAsync(ct));

        return true;
    }

    /// <summary>
    /// Zamienia „nie ma takiego zasobu" na nasz własny rodzaj wyjątku.
    /// </summary>
    /// <remarks>
    /// Google oddaje na to dwa kody: 410 przy wydarzeniu skasowanym i 404 przy takim,
    /// którego nigdy nie było albo do którego nie mamy dostępu. Dla zapisu znaczą to
    /// samo — nie ma czego zmienić — a rozróżnianie ich na ekranie byłoby dzieleniem
    /// włosa tam, gdzie odpowiedź i tak jest jedna.
    ///
    /// Tłumaczenie tutaj, a nie w warstwie aplikacji: to jedyne miejsce, które w ogóle
    /// widzi wyjątki Google, i jedyne, które ma prawo je znać.
    /// </remarks>
    private static async Task Gone(Func<Task> patch)
    {
        try
        {
            await patch();
        }
        catch (GoogleApiException e)
            when (e.HttpStatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            throw new EventGone();
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(ReadOnlyMessage, e);
        }
    }

    /// <summary>
    /// Odmowa zapisu po polsku.
    /// </summary>
    /// <remarks>
    /// Google oddaje na to zdanie po angielsku, z kodem stanu w środku: „The service
    /// calendar has thrown an exception. HttpStatusCode is Forbidden." Na karcie
    /// wydarzenia wyglądało to jak awaria aplikacji, a jest zwyczajną i trwałą
    /// odpowiedzią: do tego kalendarza nie wolno pisać i powtórzenie tego nie zmieni.
    /// Kalendarze świąteczne, fazy księżyca i cudze udostępnione bez prawa zmian są
    /// właśnie takie — a wpisuje się je akurat po to, żeby je tylko czytać.
    /// </remarks>
    private const string ReadOnlyMessage =
        "Ten kalendarz jest tylko do odczytu — Google nie pozwala w nim nic zmieniać "
        + "ani kasować. Tak są ustawione kalendarze świąteczne, fazy księżyca i cudze "
        + "udostępnione bez prawa zmian.";

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
    private static string Basic(string externalId) =>
        externalId.Split('|', 2)[0];

    private static Event Build(CalendarDraft draft) => new()
    {
        Summary = draft.Title,
        Location = draft.Location,
        Start = When(draft.Start, draft.AllDay),
        End = When(draft.End, draft.AllDay),
    };

    /// <summary>
    /// Chwila albo data — Google trzyma to w dwóch różnych polach.
    /// </summary>
    /// <remarks>
    /// Całodniowe idzie jako sama data, bez strefy, bo data strefy nie ma. Wysłane
    /// jako chwila o północy czasu lokalnego wypadałoby u kogoś na wschód dzień
    /// wcześniej — a „wzięte na dziś" ma znaczyć dziś u każdego, kto to widzi.
    /// </remarks>
    private static EventDateTime When(DateTimeOffset moment, bool allDay) =>
        allDay
            ? new EventDateTime { Date = moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : new EventDateTime { DateTimeDateTimeOffset = moment };

    /// <summary>Usługa konta — głównego, gdy konto puste.</summary>
    private async Task<CalendarService> ServiceAsync(string? account, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            await ComposeAsync(account, ct);
            return _services[Key(account)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Żeton dodatkowego konta — tylko ten już leżący na tym urządzeniu.
    /// </summary>
    /// <remarks>
    /// Zgoda dla konta dodatkowego przychodzi wyłącznie z przycisku „Dodaj konto
    /// Google". Tutaj, czyli przy pobieraniu, jej brak to nie powód do otwierania
    /// przeglądarki, tylko zdanie do przeczytania: podłączenia kalendarzy jadą między
    /// urządzeniami, żetony nie, więc drugie urządzenie normalnie widzi kalendarz,
    /// do którego nie ma jeszcze zgody. Okno zgody wyskakujące samo w środku
    /// odświeżania wygląda jak awaria, a na Androidzie nie wygląda wcale — po prostu
    /// zawisa.
    /// </remarks>
    private async Task<UserCredential> AccountConsentAsync(
        string folder, string account, CancellationToken ct)
    {
        var tokenKey = GoogleDriveFactory.AccountKey(account);

        if (!await GoogleDriveFactory.HasTokenAsync(folder, tokenKey, ct))
        {
            throw new InvalidOperationException(
                $"to urządzenie nie ma jeszcze zgody konta {account}. Otwórz Ustawienia, "
                + "sekcja Kalendarze, i kliknij „Dodaj konto Google”, logując się na to konto.");
        }

        return await GoogleDriveFactory.AuthorizeCalendarAsync(
            settings.GoogleClientId!, settings.GoogleClientSecret!, folder, tokenKey, ct);
    }

    /// <summary>
    /// Poziom dostępu Google na odpowiedź „czy wolno tu pisać".
    /// </summary>
    /// <remarks>
    /// Google nazywa cztery: <c>owner</c>, <c>writer</c>, <c>reader</c>
    /// i <c>freeBusyReader</c>. Pisać wolno dwóm pierwszym. Nierozpoznane znaczy
    /// „wolno": nowa nazwa poziomu zablokowałaby zapis do kalendarza, do którego wolno,
    /// a objawem byłoby pole nie do kliknięcia bez żadnego wyjaśnienia.
    /// </remarks>
    private static bool IsReadOnly(string? level) =>
        level is { Length: > 0 }
            && !string.Equals(level, "owner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(level, "writer", StringComparison.OrdinalIgnoreCase);

    private static string Key(string? account) =>
        string.IsNullOrWhiteSpace(account) ? string.Empty : account.Trim();

    private async Task<GoogleCalendarFeed> ConnectAsync(string? account, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return await ComposeAsync(account, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GoogleCalendarFeed> ComposeAsync(string? account, CancellationToken ct)
    {
        var key = Key(account);

        if (_feeds.TryGetValue(key, out var ready))
        {
            return ready;
        }

        CheckCredentials();

        if (!settings.GoogleCalendarEnabled)
        {
            throw new InvalidOperationException(
                "zgoda obejmuje tylko Dysk. Zaznacz „Czytaj i zmieniaj mój kalendarz Google” "
                + "i kliknij „Zapisz i zsynchronizuj”, żeby poprosić o dostęp do kalendarza.");
        }

        var folder = Path.Combine(Path.GetDirectoryName(databasePath)!, "google");

        // Konto główne idzie starą drogą — tym samym kluczem żetonu, co przed kontami
        // dodatkowymi. Dzięki temu nikt, kto ma już zgodę, nie musi jej dawać od nowa.
        var credential = key.Length == 0
            ? await GoogleDriveFactory.AuthorizeAsync(
                settings.GoogleClientId!, settings.GoogleClientSecret!, folder,
                withCalendar: true, ct)
            : await AccountConsentAsync(folder, key, ct);

        var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Marshal",
        });

        _services[key] = service;

        return _feeds[key] = new GoogleCalendarFeed(service);
    }
}
