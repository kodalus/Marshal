namespace Marshal.Application.Calendar;

/// <summary>
/// Tożsamość wydarzenia cyklicznego w Google — <b>wyliczana z serii, nie nadawana</b>.
/// </summary>
/// <remarks>
/// <para>
/// Ta sama lekcja, co przy tożsamości wystąpienia, tyle że o warstwę wyżej — i raz już
/// przeoczona. Pisałam, że przy jednym wydarzeniu na serię pytanie „które urządzenie
/// wysyła" znika, bo oba wysyłają to samo. To było prawdą o <b>treści</b> i nieprawdą
/// o <b>tożsamości</b>: zakładając wydarzenie bez podanego identyfikatora, każde
/// urządzenie dostawało od Google własny. Pulpit i telefon dociągają serie niezależnie,
/// więc zanim wskazanie dojechało synchronizacją, w kalendarzu stały dwa wydarzenia
/// cykliczne na jedną serię — a Marshal znał tylko jedno z nich i drugie rysował obok
/// jako cudze. Sześćdziesiąt duplikatów zamieniło się w jeden i to był cały zysk.
/// </para>
/// <para>
/// Identyfikator podany przy zakładaniu rozstrzyga to z definicji: oba urządzenia proponują
/// ten sam, więc drugie dostaje od Google odmowę „taki już jest" i przyjmuje go za swój.
/// Nie ma wyścigu, bo nie ma czego wygrywać.
/// </para>
/// <para>
/// Postać narzuca Google: dozwolone są wyłącznie znaki kodowania base32hex — cyfry
/// i małe litery od „a" do „v" — a długość mieści się w 5–1024. Szesnaście bajtów
/// tożsamości serii daje równo dwadzieścia sześć znaków.
/// </para>
/// </remarks>
public static class SeriesEventId
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuv";

    public static string For(Guid series)
    {
        // Bajty w kolejności sieciowej, tak samo jak przy tożsamości wystąpienia:
        // zapis ma wyjść identycznie niezależnie od tego, jak procesor układa liczby.
        var bytes = series.ToByteArray(bigEndian: true);
        var chars = new char[26];

        var value = 0;
        var bits = 0;
        var at = 0;

        foreach (var piece in bytes)
        {
            value = (value << 8) | piece;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                chars[at++] = Alphabet[(value >> bits) & 31];
            }
        }

        // Sto dwadzieścia osiem bitów to dwadzieścia pięć pełnych piątek i trzy bity
        // reszty — dopełnione zerami, żeby nie zgubić ostatniego znaku.
        if (bits > 0)
        {
            chars[at++] = Alphabet[(value << (5 - bits)) & 31];
        }

        return new string(chars, 0, at);
    }
}
