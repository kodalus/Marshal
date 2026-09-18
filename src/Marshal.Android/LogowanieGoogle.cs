using System.Net;
using System.Net.Sockets;
using System.Text;
using Android.Content;
using Android.OS;
using Android.Widget;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Marshal.Infrastructure.Sync.Google;

namespace Marshal.Android;

/// <summary>
/// Odebranie zgody Google na Androidzie.
/// </summary>
/// <remarks>
/// <para>
/// Domyślna droga biblioteki Google robi dwie rzeczy: otwiera przeglądarkę
/// uruchomieniem procesu i czeka na powrót na porcie pętli zwrotnej. Na Androidzie
/// pierwsza nie istnieje — procesów się tu nie uruchamia, otwiera się zamiar.
/// <b>Druga istnieje i działa</b>, bo przeglądarka telefonu sięga do pętli zwrotnej
/// tego samego telefonu. Dlatego wymieniona jest tylko połowa, a nie cała droga.
/// </para>
/// <para>
/// <b>Co to znaczy dla poświadczeń:</b> wystarczają te same, których używa komputer —
/// typu „aplikacja komputerowa". Nie trzeba zakładać poświadczeń typu Android
/// z odciskiem podpisu APK, nie trzeba podpisywać wydania stałym kluczem i nie
/// trzeba rejestrować własnego schematu adresu. Google pozwala klientom typu
/// komputerowego wracać na dowolny port pętli zwrotnej i nie wymaga zgłaszania go
/// z góry.
/// </para>
/// <para>
/// Nasłuch stoi na gnieździe, a nie na <c>HttpListener</c>. Powód jest prozaiczny:
/// tu nie ma jak sprawdzić, czy ta klasa działa na Androidzie, a rozmowa sprowadza
/// się do przeczytania jednego wiersza żądania i odpisania jedną stroną. Mniej
/// niewiadomych za cenę trzydziestu wierszy.
/// </para>
/// </remarks>
internal sealed class OdbiorcaKoduAndroid(Context kontekst) : ICodeReceiver
{
    /// <summary>
    /// Ile czekać na powrót z przeglądarki.
    /// </summary>
    /// <remarks>
    /// Czekanie bez końca jest tu najgorszym z możliwych zachowań: nie widać, czy
    /// zgoda się nie udała, czy trwa, czy aplikacja o niej zapomniała — a jedyne,
    /// co można zrobić, to zamknąć wszystko i nie dowiedzieć się niczego. Po tym
    /// czasie zamiast wiszenia jest zdanie mówiące, gdzie stał nasłuch i co sprawdzić.
    /// </remarks>
    private static readonly TimeSpan Cierpliwosc = TimeSpan.FromMinutes(3);

    private readonly int _port = WolnyPort();

    public string RedirectUri => $"http://127.0.0.1:{_port}/authorize/";

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(
        AuthorizationCodeRequestUrl url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        var nasluch = new TcpListener(IPAddress.Loopback, _port);
        nasluch.Start();

        // Własny zegar doliczony do odwołania z zewnątrz: bez niego czekanie nie ma końca.
        using var zegar = CancellationTokenSource.CreateLinkedTokenSource(ct);
        zegar.CancelAfter(Cierpliwosc);

        // Zatrzymanie nasłuchu jest tu jedyną drogą przerwania czekania: gniazdo
        // w trakcie przyjmowania połączenia nie ogląda się na znacznik odwołania.
        await using var przerwanie = zegar.Token.Register(nasluch.Stop);

        // Druga droga powrotu, na wypadek gdyby pierwsza nie doszła: adres przepisany
        // z paska przeglądarki. Zgłoszona **przed** otwarciem przeglądarki, żeby okno
        // ustawień miało co pokazać od pierwszej chwili.
        var reczny = PowrotZgody.Czekaj();

        // Usługa pierwszoplanowa na czas czekania: bez niej proces idzie do zamrażarki,
        // gdy przeglądarka go przykryje, i powrót ze zgody nie ma kogo obudzić.
        UslugaLogowania.Pilnuj(kontekst, wlacz: true);

        try
        {
            Otworz(url.Build());

            // Dymek, bo przeglądarka przykrywa aplikację i bez niego nie widać, czy
            // Marshal w ogóle doszedł do tego kroku. Przy nieudanym powrocie to jest
            // pierwsza rzecz, którą trzeba wiedzieć.
            Powiedz($"Czekam na zgodę Google — 127.0.0.1:{_port}");

            var gniazdo = Gniazdo(nasluch, zegar.Token);

            if (await Task.WhenAny(gniazdo, reczny) == reczny)
            {
                // Gniazdo zostaje porzucone i zaraz zgaśnie razem z nasłuchem.
                // Jego wyjątek trzeba obejrzeć, inaczej wraca później jako nieobsłużony.
                _ = gniazdo.ContinueWith(
                    zadanie => _ = zadanie.Exception, TaskScheduler.Default);

                return new AuthorizationCodeResponseUrl(Pola(await reczny));
            }

            return await gniazdo;
        }
        // Pełna nazwa, bo Android.OS ma własny typ o tej samej nazwie.
        catch (System.OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Skończyła się cierpliwość, a nie zostało to przerwane z zewnątrz.
            // Wyjątek z treścią, bo ląduje pod przyciskiem synchronizacji i jest
            // jedyną rzeczą, jaką widać po nieudanym logowaniu.
            throw new TimeoutException(
                $"Przeglądarka nie wróciła ze zgodą w ciągu {Cierpliwosc.TotalMinutes:0} minut. "
                + $"Nasłuch stał na 127.0.0.1:{_port}. Jeśli przeglądarka pokazała stronę "
                + "z błędem połączenia, znaczy to, że nie dotarła z powrotem do aplikacji.");
        }
        finally
        {
            UslugaLogowania.Pilnuj(kontekst, wlacz: false);
            PowrotZgody.Przestan();
            nasluch.Stop();
        }
    }

    /// <summary>Powrót przeglądarki na port pętli zwrotnej — droga pierwsza.</summary>
    private async Task<AuthorizationCodeResponseUrl> Gniazdo(
        TcpListener nasluch, CancellationToken ct)
    {
        while (true)
        {
            using var polaczenie = await nasluch.AcceptTcpClientAsync(ct);
            var strumien = polaczenie.GetStream();

            if (await Zadanie(strumien, ct) is not { } adres)
            {
                continue;
            }

            var pola = Pola(adres);

            // Przeglądarka pyta też o inne rzeczy, choćby o ikonę strony.
            // Żądanie bez kodu i bez błędu nie jest powrotem ze zgody.
            if (!pola.ContainsKey("code") && !pola.ContainsKey("error"))
            {
                await Odpisz(strumien, "Marshal czeka na zgodę.", ct);
                continue;
            }

            await Odpisz(strumien, "Zgoda przyjęta. Możesz wrócić do Marshala.", ct);

            // Powrót do aplikacji sam, bez szukania jej w przełączniku okien.
            Wroc();

            return new AuthorizationCodeResponseUrl(pola);
        }
    }

    /// <summary>
    /// Dymek systemowy. Przez główną pętlę, bo czekanie na zgodę siedzi na wątku
    /// roboczym, a dymek wywołany spoza głównego wątku kończy się wyjątkiem.
    /// </summary>
    private void Powiedz(string tresc) =>
        new Handler(Looper.MainLooper!).Post(
            () => Toast.MakeText(kontekst, tresc, ToastLength.Long)?.Show());

    /// <summary>Port wybrany przez system. Zajęty na stałe byłby zajęty akurat wtedy, gdy trzeba.</summary>
    private static int WolnyPort()
    {
        var probne = new TcpListener(IPAddress.Loopback, 0);
        probne.Start();
        var port = ((IPEndPoint)probne.LocalEndpoint).Port;
        probne.Stop();
        return port;
    }

    private void Otworz(Uri adres)
    {
        var zamiar = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(adres.ToString()));

        // Nowe zadanie, bo kontekst jest aplikacji, a nie okna. Bez tego system odmawia.
        zamiar.AddFlags(ActivityFlags.NewTask);
        kontekst.StartActivity(zamiar);
    }

    private void Wroc()
    {
        var zamiar = new Intent(kontekst, typeof(MainActivity));
        zamiar.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        kontekst.StartActivity(zamiar);
    }

    /// <summary>Adres z pierwszego wiersza żądania. Reszta rozmowy nas nie obchodzi.</summary>
    private static async Task<string?> Zadanie(NetworkStream strumien, CancellationToken ct)
    {
        var bufor = new byte[4096];
        var ile = await strumien.ReadAsync(bufor, ct);

        if (ile <= 0)
        {
            return null;
        }

        var wiersz = Encoding.UTF8.GetString(bufor, 0, ile).Split('\n')[0].Split(' ');

        // „GET /authorize/?code=… HTTP/1.1"
        return wiersz.Length >= 2 ? wiersz[1] : null;
    }

    private static Dictionary<string, string> Pola(string adres)
    {
        var pytajnik = adres.IndexOf('?');

        if (pytajnik < 0)
        {
            return new Dictionary<string, string>();
        }

        return adres[(pytajnik + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(para => para.Split('=', 2))
            .ToDictionary(
                para => Uri.UnescapeDataString(para[0]),
                para => para.Length > 1 ? Uri.UnescapeDataString(para[1].Replace('+', ' ')) : string.Empty);
    }

    private static async Task Odpisz(NetworkStream strumien, string tresc, CancellationToken ct)
    {
        var strona = $"<!doctype html><html lang=\"pl\"><meta charset=\"utf-8\">"
            + "<title>Marshal</title>"
            + "<body style=\"background:#121729;color:#fff;font-family:sans-serif;"
            + $"display:flex;align-items:center;justify-content:center;height:100vh\">{tresc}</body></html>";

        var bajty = Encoding.UTF8.GetBytes(strona);

        var naglowek = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {bajty.Length}\r\nConnection: close\r\n\r\n");

        await strumien.WriteAsync(naglowek, ct);
        await strumien.WriteAsync(bajty, ct);
        await strumien.FlushAsync(ct);
    }
}
