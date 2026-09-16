using FluentAssertions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
using Xunit;

namespace Marshal.Tests;

public class ProjectTests
{
    private static readonly DateTimeOffset Teraz = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));

    private static Project Projekt(string wynik = "Zimowe opony są na aucie", Guid? obszar = null) =>
        new(Guid.CreateVersion7(), Teraz, new Hlc(1000, 0, "a"), wynik, obszar ?? Guid.CreateVersion7(), 1.0);

    [Fact]
    public void Nowy_projekt_jest_aktywny()
    {
        Projekt().State.Should().Be(ProjectState.Active);
    }

    [Fact]
    public void Projekt_bez_obszaru_jest_odrzucany()
    {
        var utworz = () => new Project(
            Guid.CreateVersion7(), Teraz, new Hlc(1000, 0, "a"), "Cokolwiek", Guid.Empty, 1.0);

        utworz.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Projekt_bez_opisu_wyniku_jest_odrzucany(string wynik)
    {
        var utworz = () => Projekt(wynik);

        utworz.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Podprojekt_przejmuje_obszar_nadrzednego()
    {
        var obszarCelu = Guid.CreateVersion7();
        var cel = Projekt("Prawo jazdy jest w portfelu", obszarCelu);
        var krok = Projekt("Egzamin teoretyczny zdany", Guid.CreateVersion7());

        krok.AttachTo(cel, new Hlc(2000, 0, "a"));

        krok.ParentProjectId.Should().Be(cel.Id);
        krok.AreaId.Should().Be(obszarCelu);
    }

    [Fact]
    public void Projekt_nie_moze_byc_wlasnym_nadrzednym()
    {
        var projekt = Projekt();

        var samo = () => projekt.AttachTo(projekt, new Hlc(2000, 0, "a"));

        samo.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Odpiecie_zostawia_obszar_bez_zmian()
    {
        var cel = Projekt("Cel", Guid.CreateVersion7());
        var krok = Projekt("Krok", Guid.CreateVersion7());
        krok.AttachTo(cel, new Hlc(2000, 0, "a"));

        krok.Detach(new Hlc(3000, 0, "a"));

        krok.ParentProjectId.Should().BeNull();
        krok.AreaId.Should().Be(cel.AreaId);
    }
}
