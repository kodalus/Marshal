using FluentAssertions;
using Marshal.Domain.Calendar;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Tożsamość zadania przejmującego wydarzenie — wyliczana z kalendarza i wydarzenia.
/// </summary>
/// <remarks>
/// Ta sama zasada, co przy wystąpieniu serii i przy wydarzeniu cyklicznym w Google:
/// przejąć ten sam wpis można na dwóch urządzeniach, zanim którekolwiek zdąży o tym
/// powiedzieć drugiemu. Przy losowanej tożsamości powstałyby dwa zadania wskazujące
/// jedno wydarzenie — a scalanie nie miałoby czego scalić.
/// </remarks>
public sealed class AdoptedIdTests
{
    private static readonly Guid Calendar = Guid.CreateVersion7();

    [Fact]
    public void To_samo_wydarzenie_daje_to_samo_zadanie()
    {
        AdoptedId.For(Calendar, "sen-2026-10-22")
            .Should().Be(AdoptedId.For(Calendar, "sen-2026-10-22"));
    }

    [Fact]
    public void Inne_wydarzenie_daje_inne_zadanie()
    {
        AdoptedId.For(Calendar, "sen-2026-10-22")
            .Should().NotBe(AdoptedId.For(Calendar, "sen-2026-10-23"));
    }

    [Fact]
    public void To_samo_wydarzenie_w_innym_kalendarzu_daje_inne_zadanie()
    {
        // U Google tożsamością wpisu jest para: kalendarz i identyfikator. Ten sam
        // identyfikator w dwóch kalendarzach to dwa różne wpisy.
        AdoptedId.For(Calendar, "sen")
            .Should().NotBe(AdoptedId.For(Guid.CreateVersion7(), "sen"));
    }

    [Fact]
    public void Tozsamosc_jest_poprawnym_identyfikatorem()
    {
        // Baza i synchronizacja mają prawo zakładać, że to jest UUID, a nie szesnaście
        // przypadkowych bajtów.
        var id = AdoptedId.For(Calendar, "sen");

        id.Should().NotBe(Guid.Empty);
        id.Version.Should().Be(8, "wyliczona z nazwy, nie losowana");
    }
}
