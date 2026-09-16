using FluentAssertions;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;

namespace Marshal.Tests;

public class AreaTests
{
    private static readonly DateTimeOffset Kiedys = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));

    private static Area Obszar(string nazwa = "Zdrowie") =>
        new(Guid.CreateVersion7(), Kiedys, new Hlc(1000, 0, "a"), nazwa, sortOrder: 1.0);

    [Fact]
    public void Nowy_obszar_jest_aktywny_i_ma_progi_domyslne()
    {
        var obszar = Obszar();

        obszar.IsActive.Should().BeTrue();
        obszar.Deleted.Should().BeFalse();
        obszar.QuietDays.Should().Be(Area.DefaultQuietDays);
        obszar.DefaultNudgeDays.Should().Be(Area.DefaultNudgeDaysValue);
    }

    [Fact]
    public void Nazwa_jest_przycinana_z_bialych_znakow()
    {
        Obszar("  Sprawy urzędowe  ").Name.Should().Be("Sprawy urzędowe");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Pusta_nazwa_jest_odrzucana(string nazwa)
    {
        var utworz = () => Obszar(nazwa);

        utworz.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Progi_musza_byc_dodatnie()
    {
        var obszar = Obszar();

        var zeroweCiche = () => obszar.SetThresholds(0, 7, new Hlc(2000, 0, "a"));

        zeroweCiche.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Zmiana_pola_podnosi_znacznik_zmiany()
    {
        var obszar = Obszar();
        var przed = obszar.UpdatedAt;

        obszar.Rename("Zdrowie moje", new Hlc(2000, 0, "a"));

        obszar.UpdatedAt.Should().BeGreaterThan(przed);
    }

    [Fact]
    public void Znacznik_nie_moze_sie_cofnac()
    {
        var obszar = Obszar();

        var cofnij = () => obszar.Rename("Cokolwiek", new Hlc(500, 0, "a"));

        cofnij.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Usuniecie_jest_logiczne_i_odwracalne()
    {
        var obszar = Obszar();

        obszar.MarkDeleted(new Hlc(2000, 0, "a"));
        obszar.Deleted.Should().BeTrue();

        obszar.Restore(new Hlc(3000, 0, "a"));
        obszar.Deleted.Should().BeFalse();
    }
}
