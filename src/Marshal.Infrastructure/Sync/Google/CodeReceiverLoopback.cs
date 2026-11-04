using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;

namespace Marshal.Infrastructure.Sync.Google;

/// <summary>
/// Odebranie zgody Google na komputerze — drogą biblioteki, ale z adresem na wierzchu.
/// </summary>
/// <remarks>
/// <para>
/// Nasłuch na pętli zwrotnej i cała rozmowa z Google zostają takie, jakie były:
/// robi je <see cref="LocalServerCodeReceiver"/> z biblioteki i nie ma powodu pisać
/// tego jeszcze raz. Dołożona jest jedna rzecz — <b>adres zgody ogłoszony, zanim
/// zacznie się czekanie</b>.
/// </para>
/// <para>
/// Po co: otwarcie przeglądarki robi biblioteka uruchomieniem procesu systemowego,
/// a ten potrafi nie zrobić nic i niczego nie zgłosić. Z aplikacji wygląda to
/// identycznie jak przeglądarka otwarta i porzucona — nasłuch stoi, nikt nie wraca,
/// po pięciu minutach „przerwane". Dopóki adresu nie było widać, te dwie przyczyny
/// nie dawały się rozdzielić <b>ani</b> obejść: nie było czego wkleić do przeglądarki
/// z ręki.
/// </para>
/// <para>
/// Nic poza tym się nie zmienia. Powrót idzie tą samą pętlą zwrotną i tym samym
/// portem, więc adres otwarty z ręki kończy się dokładnie tak samo, jak otwarty sam.
/// </para>
/// </remarks>
public sealed class CodeReceiverLoopback : ICodeReceiver
{
    private readonly LocalServerCodeReceiver _inner = new();

    public string RedirectUri => _inner.RedirectUri;

    public Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(
        AuthorizationCodeRequestUrl url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        ConsentReturn.Offer(url.Build().ToString());

        return _inner.ReceiveCodeAsync(url, ct);
    }
}
