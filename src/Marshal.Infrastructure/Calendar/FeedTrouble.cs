using System.Net;
using Google;

namespace Marshal.Infrastructure.Calendar;

/// <summary>
/// Powód nieudanego pobrania kalendarza, powiedziany zdaniem.
/// </summary>
/// <remarks>
/// <para>
/// Powód wychodzi na ekran, a nie tylko do dziennika — więc musi być zdaniem, a nie
/// zrzutem z biblioteki. Google zgłasza błąd tekstem na cztery linijki, zaczynającym
/// się od nazwy własnej klasy wyjątku: wklejony pod kalendarz mówi tyle, co nic,
/// a zajmuje pół ekranu.
/// </para>
/// <para>
/// Stan odpowiedzi jest tu całą wiedzą, bo rozstrzyga o tym, co z tym zrobić: 401 to
/// „podłącz konto jeszcze raz", 403 to „udostępnienie przestało obowiązywać", 404 to
/// „tego kalendarza już nie ma", a 5xx to „nie z twojej strony, spróbuj później".
/// Cztery różne czynności, które bez rozdzielenia wyglądają jak jedna awaria.
/// </para>
/// <para>
/// Czego tu nie ma: zgadywania. Nieznany przypadek oddaje pierwszą sensowną linijkę
/// tego, co przyszło — krótka prawda jest lepsza od uprzejmego zdania, które nie
/// odpowiada na pytanie „dlaczego".
/// </para>
/// </remarks>
public static class FeedTrouble
{
    /// <summary>Najdłuższy powód, jaki mieści się pod kalendarzem bez zasłaniania go.</summary>
    private const int Longest = 160;

    public static string Say(Exception trouble)
    {
        ArgumentNullException.ThrowIfNull(trouble);

        return trouble switch
        {
            GoogleApiException google => FromGoogle(google),
            HttpRequestException => "nie udało się połączyć z serwerem kalendarza.",
            _ => Shortly(trouble.Message),
        };
    }

    private static string FromGoogle(GoogleApiException trouble) => trouble.HttpStatusCode switch
    {
        HttpStatusCode.Unauthorized =>
            "Google nie przyjęło logowania (401). Podłącz konto jeszcze raz w ustawieniach.",
        HttpStatusCode.Forbidden =>
            "brak dostępu (403). Sprawdź, czy udostępnienie tego kalendarza nadal obowiązuje.",
        HttpStatusCode.NotFound =>
            "tego kalendarza nie ma już po stronie Google (404).",
        HttpStatusCode.TooManyRequests =>
            "za dużo zapytań do Google (429). Następne pobranie powinno się udać.",
        >= HttpStatusCode.InternalServerError =>
            $"Google chwilowo nie odpowiada ({(int)trouble.HttpStatusCode}).",
        _ => Shortly(trouble.Error?.Message ?? trouble.Message),
    };

    /// <summary>Pierwsza linijka niosąca treść, bez nazw klas i bez ogona na pół ekranu.</summary>
    private static string Shortly(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "pobranie nie doszło do skutku.";
        }

        // Nazwa klasy wyjątku w pierwszej linijce jest wiadomością dla programisty,
        // nie dla patrzącego. Bierzemy pierwszą linijkę, która ma w sobie odstęp —
        // czyli pierwszą, która jest zdaniem, a nie identyfikatorem.
        var lines = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var said = Array.Find(lines, line => line.Contains(' ', StringComparison.Ordinal))
            ?? lines.FirstOrDefault()
            ?? message.Trim();

        return said.Length > Longest ? $"{said[..Longest].TrimEnd()}…" : said;
    }
}
