using System.Text.Json.Nodes;
using FluentAssertions;
using Marshal.Domain.Sync;
using Xunit;

namespace Marshal.Tests;

public class ChangeLineTests
{
    private static ChangeLine Wiersz() => new()
    {
        Entity = "Tasks",
        Id = "0199ab00-0000-7000-8000-000000000001",
        Hlc = "1757942400123.000007.biurko",
        Fields = new Dictionary<string, JsonNode?>
        {
            ["Title"] = JsonValue.Create("zażółć gęślą jaźń"),
            ["State"] = JsonValue.Create(1),
            ["Deadline"] = null,
        },
    };

    [Fact]
    public void Zapis_i_odczyt_sa_wzajemnie_odwrotne()
    {
        var odczytany = ChangeLine.TryParse(Wiersz().Serialize());

        odczytany.Should().NotBeNull();
        odczytany!.Entity.Should().Be("Tasks");
        odczytany.Hlc.Should().Be("1757942400123.000007.biurko");
        odczytany.Fields["Title"]!.GetValue<string>().Should().Be("zażółć gęślą jaźń");
        odczytany.Fields["State"]!.GetValue<int>().Should().Be(1);
        odczytany.Fields["Deadline"].Should().BeNull();
    }

    [Fact]
    public void Wiersz_jest_jedna_linia()
    {
        Wiersz().Serialize().Should().NotContain("\n");
    }

    [Fact]
    public void Polskie_znaki_zostaja_czytelne()
    {
        var tekst = Wiersz().Serialize();

        tekst.Should().Contain("zażółć gęślą jaźń");
        tekst.Should().NotContain("\\u");
    }

    [Fact]
    public void Nazwy_pol_sa_krotkie()
    {
        // Plik rośnie z każdą zmianą i leży w chmurze — nazwy kluczy powtarzają się
        // w każdej linii, więc ich długość ma znaczenie przy tysiącach wpisów.
        var tekst = Wiersz().Serialize();

        tekst.Should().StartWith("{\"e\":");
        tekst.Should().Contain("\"id\":");
        tekst.Should().Contain("\"hlc\":");
        tekst.Should().Contain("\"f\":");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("to nie jest JSON")]
    [InlineData("{\"e\":\"Tasks\"")]
    [InlineData("{\"e\":\"Tasks\",\"id\":\"x\"}")]
    [InlineData("{\"id\":\"x\",\"hlc\":\"1.0.a\"}")]
    [InlineData("{\"e\":\"Tasks\",\"hlc\":\"1.0.a\"}")]
    public void Uszkodzony_wiersz_zwraca_null_zamiast_wybuchac(string linia)
    {
        // Plik może zostać ucięty w pół linii przez przerwane pobieranie albo
        // pochodzić z nowszej wersji aplikacji. Przerwanie całości oznaczałoby,
        // że jedna zła linia blokuje wszystkie zmiany, które przyszły po niej.
        ChangeLine.TryParse(linia).Should().BeNull();
    }

    [Fact]
    public void Nieznane_pole_nie_psuje_odczytu()
    {
        // Nowsza wersja aplikacji może dopisać kolumnę, której ta jeszcze nie zna.
        var linia = "{\"e\":\"Tasks\",\"id\":\"x\",\"hlc\":\"1.0.a\",\"f\":{\"CosNowego\":42}}";

        var odczytany = ChangeLine.TryParse(linia);

        odczytany.Should().NotBeNull();
        odczytany!.Fields.Should().ContainKey("CosNowego");
    }
}
