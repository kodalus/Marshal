using FluentAssertions;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Warunki, w których aplikacja w ogóle umie liczyć czas.
/// </summary>
/// <remarks>
/// <para>
/// 17.09: kalendarz pokazywał wszystko o dwie godziny za wcześnie — wydarzenia,
/// zadania i kreskę bieżącej godziny. Przyczyną nie był żaden z przeliczników, tylko
/// jedna linijka w <c>Directory.Build.props</c>: <c>InvariantGlobalization</c>.
/// W tym trybie .NET nie ma bazy stref czasowych i na Windowsie jedyną znaną strefą
/// jest czas uniwersalny.
/// </para>
/// <para>
/// Najgorsze było to, że <b>CI przechodziło</b>. Na Linuksie strefy czyta się
/// z katalogu systemowego niezależnie od tego trybu, więc wszystkie testy — łącznie
/// z tym o zmianie czasu — działały. Usterka istniała wyłącznie na tej platformie,
/// na której aplikacja jest używana. Dlatego ten test nie sprawdza strefy, tylko
/// <b>sam przełącznik</b>: on jest tym, co się różni.
/// </para>
/// </remarks>
public sealed class GlobalizacjaTests
{
    [Fact]
    public void Tryb_niezmienny_jest_wylaczony()
    {
        AppContext.TryGetSwitch("System.Globalization.Invariant", out var niezmienny);

        niezmienny.Should().BeFalse(
            "bez bazy stref czasowych Windows zna tylko czas uniwersalny, "
            + "a kalendarz bez stref nie jest kalendarzem");
    }

    [Fact]
    public void Strefa_warszawska_jest_do_znalezienia()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

        // Lipiec +2, grudzień +1. Jedno i drugie, bo sama nazwa strefy nie mówi jeszcze,
        // czy przesunięcia są prawdziwe.
        zone.GetUtcOffset(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Unspecified))
            .Should().Be(TimeSpan.FromHours(2));

        zone.GetUtcOffset(new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Unspecified))
            .Should().Be(TimeSpan.FromHours(1));
    }
}
