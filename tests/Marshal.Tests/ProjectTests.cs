using FluentAssertions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
using Xunit;

namespace Marshal.Tests;

public class ProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));

    private static Project NewProject(string result = "Zimowe opony są na aucie", Guid? area = null) =>
        new(Guid.CreateVersion7(), Now, new Hlc(1000, 0, "a"), result, area ?? Guid.CreateVersion7(), 1.0);

    [Fact]
    public void Nowy_projekt_jest_aktywny()
    {
        NewProject().State.Should().Be(ProjectState.Active);
    }

    [Fact]
    public void Projekt_bez_obszaru_jest_odrzucany()
    {
        var utworz = () => new Project(
            Guid.CreateVersion7(), Now, new Hlc(1000, 0, "a"), "Cokolwiek", Guid.Empty, 1.0);

        utworz.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Projekt_bez_opisu_wyniku_jest_odrzucany(string result)
    {
        var utworz = () => NewProject(result);

        utworz.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Podprojekt_przejmuje_obszar_nadrzednego()
    {
        var targetArea = Guid.CreateVersion7();
        var target = NewProject("Prawo jazdy jest w portfelu", targetArea);
        var step = NewProject("Egzamin teoretyczny zdany", Guid.CreateVersion7());

        step.AttachTo(target, new Hlc(2000, 0, "a"));

        step.ParentProjectId.Should().Be(target.Id);
        step.AreaId.Should().Be(targetArea);
    }

    [Fact]
    public void Projekt_nie_moze_byc_wlasnym_nadrzednym()
    {
        var project = NewProject();

        var samo = () => project.AttachTo(project, new Hlc(2000, 0, "a"));

        samo.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Odpiecie_zostawia_obszar_bez_zmian()
    {
        var target = NewProject("Cel", Guid.CreateVersion7());
        var step = NewProject("Krok", Guid.CreateVersion7());
        step.AttachTo(target, new Hlc(2000, 0, "a"));

        step.Detach(new Hlc(3000, 0, "a"));

        step.ParentProjectId.Should().BeNull();
        step.AreaId.Should().Be(target.AreaId);
    }
}
