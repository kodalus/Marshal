using System.Security.Cryptography;
using System.Text;

namespace Marshal.Domain.Calendar;

/// <summary>
/// Tożsamość zadania przejmującego wydarzenie z kalendarza — <b>wyliczana</b>.
/// </summary>
/// <remarks>
/// <para>
/// Ta sama zasada, co przy wystąpieniu serii i przy wydarzeniu cyklicznym w Google,
/// i z tego samego powodu: przejąć to samo wydarzenie można na dwóch urządzeniach,
/// zanim którekolwiek zdąży o tym powiedzieć drugiemu. Przy losowanej tożsamości
/// powstałyby wtedy dwa zadania wskazujące jedno wydarzenie — a scalanie nie miałoby
/// czego scalić, bo byłyby to dwie różne rzeczy.
/// </para>
/// <para>
/// Liczona z <b>kalendarza i identyfikatora wydarzenia</b>, czyli z pary, która u Google
/// jest tożsamością wpisu. To samo wydarzenie daje więc to samo zadanie, niezależnie
/// od tego, kto je przejął i kiedy — a dwa przejęcia składają się w jedno zamiast
/// stawać obok siebie.
/// </para>
/// </remarks>
public static class AdoptedId
{
    public static Guid For(Guid calendar, string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        var name = Encoding.UTF8.GetBytes(externalId);
        var seed = new byte[16 + name.Length];

        calendar.TryWriteBytes(seed);
        name.CopyTo(seed, 16);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(seed, digest);

        var id = digest[..16];

        // Wersja i wariant wpisywane wprost — jak przy wystąpieniu serii: baza
        // i synchronizacja mają prawo zakładać, że identyfikator jest poprawnym UUID-em.
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        // Kolejność sieciowa, nie pamięciowa: zwykły konstruktor czyta pierwsze osiem
        // bajtów po swojemu i nibble wersji wypadłby w innym bajcie, niż mówi zapis.
        return new Guid(id, bigEndian: true);
    }
}
