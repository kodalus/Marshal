using FluentAssertions;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
using Xunit;

namespace Marshal.Tests;

public class ProjectTreeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    private static long _znacznik = 1000;

    private static Hlc Stamp() => new(_znacznik += 10, 0, "t");

    private static Area Obszar(string name, double order) =>
        new(Guid.CreateVersion7(), Now, Stamp(), name, order);

    private static Project Projekt(string result, Guid area, double order = 0, Guid? parent = null) =>
        new(Guid.CreateVersion7(), Now, Stamp(), result, area, order, parent);

    [Fact]
    public void Puste_obszary_i_tak_sa_widoczne()
    {
        var areas = new[] { Obszar("Praca", 1), Obszar("Dom", 2) };

        var rows = ProjectTree.Build(areas, []);

        rows.Select(w => w.Label).Should().Equal("Praca", "Dom");
        rows.Should().OnlyContain(w => w.IsArea);
    }

    [Fact]
    public void Projekty_ida_pod_swoim_obszarem()
    {
        var work = Obszar("Praca", 1);
        var dom = Obszar("Dom", 2);
        var projects = new[] { Projekt("Raport oddany", work.Id), Projekt("Opony wymienione", dom.Id) };

        var rows = ProjectTree.Build([work, dom], projects);

        rows.Select(w => w.Label).Should().Equal("Praca", "Raport oddany", "Dom", "Opony wymienione");
        rows[1].Depth.Should().Be(1);
    }

    [Fact]
    public void Podprojekt_ma_wieksza_glebokosc()
    {
        var area = Obszar("Urzędy", 1);
        var target = Projekt("Prawo jazdy jest w portfelu", area.Id);
        var step = Projekt("Egzamin zdany", area.Id, parent: target.Id);

        var rows = ProjectTree.Build([area], [target, step]);

        rows.Select(w => w.Depth).Should().Equal(0, 1, 2);
        rows.Last().Label.Should().Be("Egzamin zdany");
    }

    [Fact]
    public void Podprojekt_bez_rodzica_jest_pokazywany_jako_korzen()
    {
        // Przy synchronizacji zmiany przychodzą w kolejności zapisu, nie zależności,
        // więc podprojekt potrafi dotrzeć przed swoim celem. Ukrycie go znaczyłoby,
        // że zadanie istnieje, a nie widać go nigdzie.
        var area = Obszar("Urzędy", 1);
        var sierota = Projekt("Egzamin zdany", area.Id, parent: Guid.CreateVersion7());

        var rows = ProjectTree.Build([area], [sierota]);

        rows.Should().HaveCount(2);
        rows.Last().Label.Should().Be("Egzamin zdany");
        rows.Last().Depth.Should().Be(1);
    }

    [Fact]
    public void Cykl_nie_zapetla_budowania()
    {
        var area = Obszar("Dom", 1);
        var a = Projekt("A", area.Id);
        var b = Projekt("B", area.Id, parent: a.Id);
        a.AttachTo(b, Stamp());

        var buduj = () => ProjectTree.Build([area], [a, b]);

        buduj.Should().NotThrow();
    }

    [Fact]
    public void Projekt_pojawia_sie_tylko_raz()
    {
        var area = Obszar("Dom", 1);
        var target = Projekt("Cel", area.Id);
        var step = Projekt("Krok", area.Id, parent: target.Id);

        var rows = ProjectTree.Build([area], [target, step]);

        rows.Select(w => w.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Obszary_ida_wedlug_wlasnej_kolejnosci()
    {
        var rows = ProjectTree.Build([Obszar("Trzeci", 3), Obszar("Pierwszy", 1), Obszar("Drugi", 2)], []);

        rows.Select(w => w.Label).Should().Equal("Pierwszy", "Drugi", "Trzeci");
    }

    /// <summary>
    /// Barwa schodzi obszar → projekt → podprojekt, dopóki ktoś jej nie nadpisze.
    /// </summary>
    /// <remarks>
    /// Wiersz musi pokazywać barwę **odziedziczoną**, nie własną. Inaczej projekt bez
    /// własnego koloru wyglądałby na bezbarwny, a jego zadania byłyby na siatce
    /// kolorowe — i nie dałoby się zgadnąć, skąd ten kolor.
    /// </remarks>
    [Fact]
    public void Barwa_schodzi_z_obszaru_na_projekty()
    {
        var area = Obszar("Praca", 1);
        area.SetColor("#4E7FD8", Stamp());

        var target = Projekt("Cel", area.Id);
        var step = Projekt("Krok", area.Id, parent: target.Id);

        var rows = ProjectTree.Build([area], [target, step]);

        rows.Select(w => w.Color).Should().AllBeEquivalentTo("#4E7FD8");
    }

    [Fact]
    public void Wlasna_barwa_projektu_wygrywa_i_schodzi_nizej()
    {
        var area = Obszar("Praca", 1);
        area.SetColor("#4E7FD8", Stamp());

        var target = Projekt("Cel", area.Id);
        target.SetColor("#CF5757", Stamp());
        var step = Projekt("Krok", area.Id, parent: target.Id);

        var rows = ProjectTree.Build([area], [target, step]);

        rows.Single(w => w.Label == "Praca").Color.Should().Be("#4E7FD8");
        rows.Single(w => w.Label == "Cel").Color.Should().Be("#CF5757");
        rows.Single(w => w.Label == "Krok").Color.Should().Be("#CF5757");
    }
}
