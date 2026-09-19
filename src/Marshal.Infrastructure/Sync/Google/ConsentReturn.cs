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

    /// <summary>Czy logowanie czeka właśnie na powrót z przeglądarki.</summary>
    public static bool Waiting
    {
        get { lock (Lock) { return _waiting is not null; } }
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
        }
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
