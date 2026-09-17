using Google.Apis.Auth.OAuth2;
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
    public static readonly string CalendarScope = CalendarService.Scope.CalendarReadonly;

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
        withCalendar ? "marshal-kalendarz" : "marshal";

    /// <summary>Zgoda użytkownika. Wspólna droga dla Dysku i kalendarza.</summary>
    public static Task<UserCredential> AuthorizeAsync(
        string clientId,
        string clientSecret,
        string tokenFolder,
        bool withCalendar,
        CancellationToken ct = default)
    {
        string[] zakresy = withCalendar ? [Scope, CalendarScope] : [Scope];

        return GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            zakresy,
            UserKey(withCalendar),
            ct,
            new FileDataStore(tokenFolder, fullPath: true));
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
