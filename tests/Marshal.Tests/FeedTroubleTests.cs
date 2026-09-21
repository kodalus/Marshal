using System.Net;
using FluentAssertions;
using Google;
using Marshal.Infrastructure.Calendar;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Powód nieudanego pobrania kalendarza — napis, który trafia pod kalendarz.
/// </summary>
/// <remarks>
/// Kalendarz zewnętrzny jest jedynym miejscem w aplikacji, w którym wszystkie
/// przyczyny awarii są po cudzej stronie. Powód jest więc jedyną rzeczą, z której
/// da się cokolwiek zrobić — a przepisany z biblioteki nie mówi nic: cztery linijki
/// zaczynające się od nazwy klasy wyjątku.
/// </remarks>
public sealed class FeedTroubleTests
{
    [Fact]
    public void Brak_dostepu_mowi_o_udostepnieniu_a_nie_o_kodzie()
    {
        // 403 znaczy „kalendarz jest, ale nie dla ciebie" — czyli czynność do zrobienia
        // jest po stronie tego, kto udostępniał, a nie po stronie aplikacji.
        var said = FeedTrouble.Say(
            new GoogleApiException("Calendar", "Forbidden") { HttpStatusCode = HttpStatusCode.Forbidden });

        said.Should().Contain("403").And.Contain("udostępnienie");
    }

    [Fact]
    public void Wygasle_logowanie_mowi_co_zrobic()
    {
        var said = FeedTrouble.Say(
            new GoogleApiException("Calendar", "Unauthorized")
            {
                HttpStatusCode = HttpStatusCode.Unauthorized,
            });

        said.Should().Contain("401").And.Contain("Podłącz konto");
    }

    [Fact]
    public void Awaria_po_stronie_Google_nie_wyglada_na_nasza()
    {
        var said = FeedTrouble.Say(
            new GoogleApiException("Calendar", "Nope")
            {
                HttpStatusCode = HttpStatusCode.ServiceUnavailable,
            });

        said.Should().Contain("503").And.Contain("Google");
    }

    [Fact]
    public void Zerwana_siec_to_nie_kod_odpowiedzi()
    {
        // Kanał iCal po złym adresie i kanał bez sieci wyglądały tak samo: „nieudanych 1".
        FeedTrouble.Say(new HttpRequestException("No such host is known."))
            .Should().Contain("połączyć");
    }

    [Fact]
    public void Nieznany_powod_oddaje_pierwsze_zdanie_a_nie_nazwe_klasy()
    {
        // Tak właśnie wygląda tekst wyjątku z biblioteki Google: nazwa własna w pierwszej
        // linijce, treść w drugiej. Pierwsza linijka jest wiadomością dla programisty.
        var said = FeedTrouble.Say(
            new InvalidOperationException("Google.Apis.Requests.RequestError\nNot Found [404]\nErrors ["));

        said.Should().Be("Not Found [404]");
    }

    [Fact]
    public void Dlugi_powod_zostaje_przyciety()
    {
        // Powód zasłaniający kalendarz odpowiada na pytanie „dlaczego" kosztem pytania
        // „co dalej" — a po to się na ten ekran wchodzi.
        var said = FeedTrouble.Say(new InvalidOperationException(new string('a', 400)));

        said.Should().EndWith("…").And.HaveLength(161);
    }

    [Fact]
    public void Wyjatek_bez_tresci_nie_zostawia_pustej_linijki()
    {
        FeedTrouble.Say(new InvalidOperationException(string.Empty))
            .Should().NotBeEmpty();
    }
}
