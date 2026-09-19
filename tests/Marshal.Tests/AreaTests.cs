using FluentAssertions;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Xunit;

namespace Marshal.Tests;

public class AreaTests
{
    private static readonly DateTimeOffset Kiedys = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));

    private static Area NewArea(string name = "Zdrowie") =>
        new(Guid.CreateVersion7(), Kiedys, new Hlc(1000, 0, "a"), name, sortOrder: 1.0);

    [Fact]
    public void Nowy_obszar_jest_aktywny_i_ma_progi_domyslne()
    {
        var area = NewArea();

        area.IsActive.Should().BeTrue();
        area.Deleted.Should().BeFalse();
        area.QuietDays.Should().Be(Area.DefaultQuietDays);
        area.DefaultNudgeDays.Should().Be(Area.DefaultNudgeDaysValue);
    }

    [Fact]
    public void Nazwa_jest_przycinana_z_bialych_znakow()
    {
        NewArea("  Sprawy urzędowe  ").Name.Should().Be("Sprawy urzędowe");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Pusta_nazwa_jest_odrzucana(string name)
    {
        var utworz = () => NewArea(name);

        utworz.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Progi_musza_byc_dodatnie()
    {
        var area = NewArea();

        var zeroweCiche = () => area.SetThresholds(0, 7, new Hlc(2000, 0, "a"));

        zeroweCiche.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Zmiana_pola_podnosi_znacznik_zmiany()
    {
        var area = NewArea();
        var before = area.UpdatedAt;

        area.Rename("Zdrowie moje", new Hlc(2000, 0, "a"));

        area.UpdatedAt.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Znacznik_nie_moze_sie_cofnac()
    {
        var area = NewArea();

        var undo = () => area.Rename("Cokolwiek", new Hlc(500, 0, "a"));

        undo.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Usuniecie_jest_logiczne_i_odwracalne()
    {
        var area = NewArea();

        area.MarkDeleted(new Hlc(2000, 0, "a"));
        area.Deleted.Should().BeTrue();

        area.Restore(new Hlc(3000, 0, "a"));
        area.Deleted.Should().BeFalse();
    }
}
