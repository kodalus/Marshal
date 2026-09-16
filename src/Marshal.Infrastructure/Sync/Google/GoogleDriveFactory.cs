using Google.Apis.Auth.OAuth2;
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
    private const string Scope = DriveService.Scope.DriveFile;

    /// <param name="clientId">Z poświadczeń OAuth typu „aplikacja na komputer".</param>
    /// <param name="tokenFolder">
    /// Katalog na odświeżalny żeton. Trafia tam tajemnica konta, więc musi leżeć
    /// w danych aplikacji użytkownika, nigdy obok dziennika synchronizacji.
    /// </param>
    public static async Task<ISyncTransportBundle> ConnectAsync(
        string clientId,
        string clientSecret,
        string tokenFolder,
        CancellationToken ct = default)
    {
        var poswiadczenie = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            [Scope],
            "marshal",
            ct,
            new FileDataStore(tokenFolder, fullPath: true));

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
