using System.Net.Mail;
using Marshal.Domain.Primitives;

namespace Marshal.Domain.Contacts;

/// <summary>
/// Osoba, której pokazuje się pojedyncze wydarzenia — imię i adres.
/// </summary>
/// <remarks>
/// <para>
/// Nie jest to książka adresowa i nie ma nią być. Google udostępnia wydarzenie jednej
/// osobie przez dopisanie jej jako gościa, a dopisanie wymaga adresu — i to wpisywanie
/// adresu za każdym razem jest jedyną rzeczą, która taką funkcję zabija. Trzy, cztery
/// osoby wystarczą: mąż, mama, wychowawczyni.
/// </para>
/// <para>
/// Encja, a nie ustawienie urządzenia, bo ma być ta sama na telefonie i na komputerze.
/// Ustawienia są lokalne z rozmysłu — strefa, motyw, wskazanie kalendarza — a lista
/// osób jest treścią, nie konfiguracją tego egzemplarza aplikacji.
/// </para>
/// </remarks>
public sealed class Contact : Entity
{
    private Contact()
    {
        Name = string.Empty;
        Email = string.Empty;
    }

    public Contact(Guid id, DateTimeOffset createdAt, Hlc updatedAt, string name, string email)
        : base(id, createdAt, updatedAt)
    {
        Name = NormalizeName(name);
        Email = NormalizeEmail(email);
    }

    /// <summary>Jak ją nazywasz. Do wyboru z listy, nie do wysłania.</summary>
    public string Name { get; private set; }

    /// <summary>Adres, pod który Google wyśle zaproszenie.</summary>
    public string Email { get; private set; }

    public void Update(string name, string email, Hlc now)
    {
        Name = NormalizeName(name);
        Email = NormalizeEmail(email);
        Touch(now);
    }

    /// <summary>
    /// Sprawdzenie adresu tutaj, a nie przy wysyłaniu.
    /// </summary>
    /// <remarks>
    /// Zły adres wychodzi na jaw dopiero u Google i wraca jako komunikat o niczym,
    /// przy czynności, która z wpisywaniem adresu nie miała już nic wspólnego. Lepiej
    /// odmówić zapisania czegoś, co na pewno nie zadziała, niż zapisać i pozwolić temu
    /// zawieść przy pierwszym użyciu.
    /// </remarks>
    private static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var czysty = email.Trim();

        if (!MailAddress.TryCreate(czysty, out _))
        {
            throw new ArgumentException($"„{czysty}” nie wygląda na adres e-mail.", nameof(email));
        }

        return czysty;
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }
}
