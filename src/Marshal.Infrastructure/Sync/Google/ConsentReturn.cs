namespace Marshal.Infrastructure.Sync.Google;

/// <summary>
/// Ręczne dokończenie zgody Google adresem przepisanym z przeglądarki.
/// </summary>
/// <remarks>
/// <para>
/// Zwykła droga wygląda tak: przeglądarka po zgodzie wraca na port pętli zwrotnej,
/// aplikacja odbiera kod z adresu i wymienia go na żeton. Na Androidzie ta droga ma
/// słaby punkt, którego nie da się obejść po naszej stronie: kiedy przeglądarka
/// przykryje aplikację, system odkłada ją do zamrażarki procesów odłożonych w tło.
/// Jądro przyjmuje wtedy połączenie, ale nikt go nie obsługuje — przeglądarka wisi
/// zamiast dostać odpowiedź, i to jest dokładnie ten objaw, po którym nie widać, że
/// chodzi o uśpiony proces.
/// </para>
/// <para>
/// Stąd druga droga, która nie zależy od niczyjej łaski: adres, na który wróciła
/// przeglądarka, widać w jej pasku. Wystarczy go przepisać. Kod zgody jest w nim
/// w całości — to ten sam ciąg, który przyszedłby przez gniazdo.
/// </para>
/// <para>
/// Stan statyczny, jak przy powiadomieniach systemowych i odbiorcy kodu: czekanie
/// żyje w środku jednego wywołania logowania, a okno ustawień jest gdzie indziej
/// i nie ma jak się do niego dostać inaczej.
/// </para>
/// </remarks>
public static class ConsentReturn
{
    private static readonly Lock Lock = new();
    private static TaskCompletionSource<string>? _waiting;

    private static string _address = string.Empty;

    /// <summary>Czy logowanie czeka właśnie na powrót z przeglądarki.</summary>
    public static bool Waiting
    {
        get { lock (Lock) { return _waiting is not null; } }
    }

    /// <summary>
    /// Adres zgody — ten, który ma się otworzyć w przeglądarce. Pusty, gdy nikt nie pyta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Otwieranie przeglądarki nie jest pewne i nie ma jak tego sprawdzić.</b>
    /// Biblioteka Google uruchamia ją procesem systemowym, a ten potrafi nie zrobić
    /// nic i niczego nie zgłosić — brak skojarzenia z przeglądarką, zasady systemowe,
    /// aplikacja uruchomiona inaczej, niż system się spodziewa. Z aplikacji widać
    /// wtedy dokładnie to samo, co przy przeglądarce otwartej i porzuconej: nasłuch
    /// stoi, nikt nie wraca, po pięciu minutach „przerwane". Dwie różne przyczyny
    /// i jeden objaw.
    /// </para>
    /// <para>
    /// Adres pokazany wprost rozdziela je i jednocześnie obie naprawia: wkleja się go
    /// do dowolnej przeglądarki, a powrót idzie tą samą pętlą zwrotną, co zawsze.
    /// Zgoda przestaje zależeć od tego, czy uruchomienie procesu się powiodło.
    /// </para>
    /// </remarks>
    public static string Address
    {
        get { lock (Lock) { return _address; } }
    }

    /// <summary>Kto otwiera przeglądarkę, ogłasza tu adres — zob. <see cref="Address"/>.</summary>
    public static event Action<string>? Offered;

    public static void Offer(string address)
    {
        lock (Lock)
        {
            _address = address;
        }

        Offered?.Invoke(address);
    }

    /// <summary>Zgłoszenie oczekiwania. Zwraca zadanie kończące się przepisanym adresem.</summary>
    public static Task<string> Wait()
    {
        lock (Lock)
        {
            // Kontynuacje asynchronicznie: bez tego dalszy ciąg logowania pobiegłby
            // na wątku okna, prosto z obsługi kliknięcia.
            _waiting = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            return _waiting.Task;
        }
    }

    /// <summary>Koniec oczekiwania — niezależnie od tego, którą drogą przyszedł kod.</summary>
    public static void Stop()
    {
        lock (Lock)
        {
            _waiting = null;
            _address = string.Empty;
        }

        Offered?.Invoke(string.Empty);
    }

    /// <summary>
    /// Podanie adresu z paska przeglądarki. Fałsz znaczy „nikt na to nie czeka".
    /// </summary>
    public static bool Serve(string address)
    {
        lock (Lock)
        {
            return _waiting?.TrySetResult(address) ?? false;
        }
    }
}
