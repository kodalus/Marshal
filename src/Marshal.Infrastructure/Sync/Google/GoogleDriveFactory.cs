using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace Marshal.Infrastructure.Sync.Google;

/// <summary>
/// Logowanie do Dysku i złożenie gotowej składnicy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Na razie tylko na komputerze.</b> Użyta tu droga logowania otwiera przeglądarkę
/// i nasłuchuje na porcie pętli zwrotnej — na Androidzie nie ma ani jednego, ani
/// drugiego. Android dostanie własne logowanie (przeglądarka systemowa i powrót przez
/// własny schemat adresu); reszta, czyli <see cref="GoogleDriveClient"/> i
/// <see cref="GoogleDriveTransport"/>, zostanie bez zmian.
/// </para>
/// <para>
/// <b>Nie sprawdzone na żywym koncie.</b>
/// </para>
/// </remarks>
public static class GoogleDriveFactory
{
    /// <summary>
    /// Wyłącznie pliki założone przez tę aplikację.
    /// </summary>
    /// <remarks>
    /// Jedyne uprawnienie Dysku, które nie wymaga przeglądu Google — a wystarcza,
    /// bo dziennik zakłada sama aplikacja. „Aplikacja" to identyfikator klienta OAuth,
    /// nie instalacja, więc drugie urządzenie z tym samym identyfikatorem i tym samym
    /// kontem widzi porcje pierwszego.
    /// </remarks>
    private static readonly string Scope = DriveService.Scope.DriveFile;

    /// <summary>
    /// Odczyt kalendarza. **Uprawnienie wrażliwe** — zob. ISettings.GoogleCalendarEnabled.
    /// </summary>
    /// <remarks>
    /// <c>static readonly</c>, nie <c>const</c>: zakresy w bibliotece Google też są polami
    /// tylko do odczytu, a nie stałymi kompilacji. Ta sama pomyłka co przy zakresie Dysku.
    /// </remarks>
    /// <remarks>
    /// Dwa uprawnienia, nie jedno pełne: <c>calendar.events</c> pozwala czytać i zmieniać
    /// wydarzenia, <c>calendar.readonly</c> — wypisać kalendarze konta (samych wydarzeń
    /// to nie obejmuje). Pełne <c>calendar</c> dołożyłoby do tego prawo do zmiany ustawień
    /// i udostępniania kalendarzy, czego ta aplikacja nie robi i nie ma powodu móc.
    /// </remarks>
    public static readonly string CalendarScope = CalendarService.Scope.CalendarReadonly;

    /// <summary>Zmiana wydarzeń. Bez tego Marshal tylko czyta.</summary>
    public static readonly string CalendarWriteScope = CalendarService.Scope.CalendarEvents;

    /// <summary>
    /// Zgoda z kalendarzem i bez niego zapisywana jest **pod osobnym kluczem**.
    /// </summary>
    /// <remarks>
    /// Biblioteka Google sprawdza, czy zapisany żeton istnieje, a nie czy obejmuje
    /// żądane uprawnienia. Przy wspólnym kluczu włączenie kalendarza oddawałoby stary
    /// żeton bez uprawnienia do kalendarza, a odmowa przychodziłaby dopiero z API,
    /// jako 403 przy pobieraniu wydarzeń — czyli w miejscu, w którym nie widać,
    /// że chodzi o zgodę. Osobny klucz wymusza świeżą zgodę i nie unieważnia starej.
    /// </remarks>
    private static string UserKey(bool withCalendar) =>
        withCalendar ? "marshal-kalendarz-zapis" : "marshal";

    /// <summary>Klucz żetonu dodatkowego konta. Adres, bo to on jest jego tożsamością.</summary>
    /// <remarks>
    /// Żeton trafia do pliku o tej nazwie, więc znaki niedopuszczalne w nazwie pliku
    /// zamieniamy na kreskę. Adres pocztowy ich nie zawiera, ale nazwa pliku budowana
    /// z cudzych danych bez sprawdzenia jest dokładnie tym rodzajem założenia, które
    /// kiedyś okazuje się nieprawdziwe.
    /// </remarks>
    public static string KluczKonta(string adres) =>
        "marshal-konto-" + new string(adres.Trim().Select(
            z => char.IsAsciiLetterOrDigit(z) || z is '@' or '.' or '-' or '_' ? z : '-').ToArray());

    /// <summary>
    /// Zgoda dodatkowego konta — <b>sam kalendarz</b>, bez Dysku.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dysk jest jeden i należy do konta głównego: to tam leży dziennik synchronizacji
    /// i tam ma zostać. Dodatkowe konto wnosi wyłącznie swoje kalendarze, więc prosi
    /// wyłącznie o nie — a zgoda, która prosi o mniej, jest zgodą, którą łatwiej dać.
    /// </para>
    /// <para>
    /// Osobny klucz żetonu, bo biblioteka rozpoznaje konta właśnie po nim. Wspólny
    /// oddawałby żeton konta głównego i drugie konto nigdy nie doszłoby do głosu.
    /// </para>
    /// </remarks>
    public static Task<UserCredential> AuthorizeCalendarAsync(
        string clientId,
        string clientSecret,
        string tokenFolder,
        string userKey,
        CancellationToken ct = default) =>
        GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            [CalendarScope, CalendarWriteScope],
            userKey,
            ct,
            new FileDataStore(tokenFolder, fullPath: true),
            OdbiorcaKodu?.Invoke());

    /// <summary>Przepisanie żetonu spod klucza tymczasowego pod docelowy.</summary>
    /// <remarks>
    /// Konta nie da się nazwać przed zgodą, bo jego adres poznajemy dopiero z listy
    /// kalendarzy — a zgody nie da się poprosić bez klucza. Stąd klucz tymczasowy
    /// na czas jednej wymiany i przepisanie żetonu, gdy adres jest już znany.
    /// Drugie proszenie o zgodę tylko po to, żeby nazwać plik, byłoby dwoma ekranami
    /// zgody na jedno konto.
    /// </remarks>
    public static async Task PrzepiszZetonAsync(
        string tokenFolder, string zKlucza, string naKlucz, CancellationToken ct = default)
    {
        var skladnica = new FileDataStore(tokenFolder, fullPath: true);

        var zeton = await skladnica.GetAsync<TokenResponse>(zKlucza)
            ?? throw new InvalidOperationException(
                "Zgoda nie zostawiła żetonu — spróbuj dodać konto jeszcze raz.");

        await skladnica.StoreAsync(naKlucz, zeton);
        await skladnica.DeleteAsync<TokenResponse>(zKlucza);
    }

    /// <summary>Czy to urządzenie ma już żeton pod tym kluczem.</summary>
    /// <remarks>
    /// Biblioteka Google na brak żetonu reaguje otwarciem przeglądarki ze zgodą.
    /// Przy odświeżaniu w tle to najgorsza możliwa reakcja: okno zgody wyskakuje
    /// samo, bez pytania, w środku innej pracy — a na Androidzie w ogóle nie ma komu
    /// go pokazać i pobieranie zawisa. Podłączenie kalendarza jedzie między
    /// urządzeniami, żeton nie, więc drugie urządzenie **z założenia** trafia na ten
    /// przypadek i ma o nim powiedzieć zdaniem, a nie ekranem.
    /// </remarks>
    public static async Task<bool> MaZetonAsync(
        string tokenFolder, string userKey, CancellationToken ct = default)
    {
        if (!Directory.Exists(tokenFolder))
        {
            return false;
        }

        try
        {
            var zeton = await new FileDataStore(tokenFolder, fullPath: true)
                .GetAsync<TokenResponse>(userKey).WaitAsync(ct);

            return zeton is not null
                && (!string.IsNullOrEmpty(zeton.RefreshToken)
                    || !string.IsNullOrEmpty(zeton.AccessToken));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nie do odczytania to nie to samo co „nie ma", ale skutek jest ten sam
            // i tak samo nie wolno na to odpowiedzieć oknem zgody.
            return false;
        }
    }

    /// <summary>
    /// Skąd wziąć kod zgody. Puste znaczy droga domyślna, czyli pulpitowa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pole statyczne, tak samo i z tego samego powodu co wyjście na powiadomienia
    /// systemowe: odebranie kodu wygląda inaczej na każdej platformie, a warstwa
    /// współdzielona nie może zależeć od żadnej z nich.
    /// </para>
    /// <para>
    /// Domyślna droga biblioteki Google otwiera przeglądarkę przez uruchomienie
    /// procesu i nasłuchuje na porcie pętli zwrotnej. Na Androidzie pierwsze nie
    /// istnieje, więc projekt platformy podstawia tu własną wersję — nasłuch zostaje
    /// ten sam, bo przeglądarka telefonu sięga do pętli zwrotnej tego samego telefonu.
    /// </para>
    /// </remarks>
    public static Func<ICodeReceiver>? OdbiorcaKodu { get; set; }

    /// <summary>Zgoda użytkownika. Wspólna droga dla Dysku i kalendarza.</summary>
    public static Task<UserCredential> AuthorizeAsync(
        string clientId,
        string clientSecret,
        string tokenFolder,
        bool withCalendar,
        CancellationToken ct = default)
    {
        string[] zakresy = withCalendar
            ? [Scope, CalendarScope, CalendarWriteScope]
            : [Scope];

        return GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            zakresy,
            UserKey(withCalendar),
            ct,
            new FileDataStore(tokenFolder, fullPath: true),
            OdbiorcaKodu?.Invoke());
    }

    /// <param name="clientId">Z poświadczeń OAuth typu „aplikacja na komputer".</param>
    /// <param name="tokenFolder">
    /// Katalog na odświeżalny żeton. Trafia tam tajemnica konta, więc musi leżeć
    /// w danych aplikacji użytkownika, nigdy obok dziennika synchronizacji.
    /// </param>
    public static async Task<ISyncTransportBundle> ConnectAsync(
        string clientId,
        string clientSecret,
        string tokenFolder,
        bool withCalendar = false,
        CancellationToken ct = default)
    {
        var poswiadczenie = await AuthorizeAsync(
            clientId, clientSecret, tokenFolder, withCalendar, ct);

        var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = poswiadczenie,
            ApplicationName = "Marshal",
        });

        return new Bundle(service, new GoogleDriveTransport(new GoogleDriveClient(service)));
    }

    private sealed record Bundle(DriveService Service, GoogleDriveTransport Transport)
        : ISyncTransportBundle
    {
        Application.Sync.ISyncTransport ISyncTransportBundle.Transport => Transport;

        public void Dispose() => Service.Dispose();
    }
}

/// <summary>Składnica razem z połączeniem, które trzeba zamknąć.</summary>
public interface ISyncTransportBundle : IDisposable
{
    Application.Sync.ISyncTransport Transport { get; }
}
