using FluentAssertions;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
using Xunit;

namespace Marshal.Tests;

public class ProjectTreeTests
{
    private static readonly DateTimeOffset Teraz = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    private static long _znacznik = 1000;

    private static Hlc Stamp() => new(_znacznik += 10, 0, "t");

    private static Area Obszar(string nazwa, double kolejnosc) =>
        new(Guid.CreateVersion7(), Teraz, Stamp(), nazwa, kolejnosc);

    private static Project Projekt(string wynik, Guid obszar, double kolejnosc = 0, Guid? rodzic = null) =>
        new(Guid.CreateVersion7(), Teraz, Stamp(), wynik, obszar, kolejnosc, rodzic);

    [Fact]
    public void Puste_obszary_i_tak_sa_widoczne()
    {
        var obszary = new[] { Obszar("Praca", 1), Obszar("Dom", 2) };

        var wiersze = ProjectTree.Build(obszary, []);

        wiersze.Select(w => w.Label).Should().Equal("Praca", "Dom");
        wiersze.Should().OnlyContain(w => w.IsArea);
    }

    [Fact]
    public void Projekty_ida_pod_swoim_obszarem()
    {
        var praca = Obszar("Praca", 1);
        var dom = Obszar("Dom", 2);
        var projekty = new[] { Projekt("Raport oddany", praca.Id), Projekt("Opony wymienione", dom.Id) };

        var wiersze = ProjectTree.Build([praca, dom], projekty);

        wiersze.Select(w => w.Label).Should().Equal("Praca", "Raport oddany", "Dom", "Opony wymienione");
        wiersze[1].Depth.Should().Be(1);
    }

    [Fact]
    public void Podprojekt_ma_wieksza_glebokosc()
    {
        var obszar = Obszar("Urzędy", 1);
        var cel = Projekt("Prawo jazdy jest w portfelu", obszar.Id);
        var krok = Projekt("Egzamin zdany", obszar.Id, rodzic: cel.Id);

        var wiersze = ProjectTree.Build([obszar], [cel, krok]);

        wiersze.Select(w => w.Depth).Should().Equal(0, 1, 2);
        wiersze.Last().Label.Should().Be("Egzamin zdany");
    }

    [Fact]
    public void Podprojekt_bez_rodzica_jest_pokazywany_jako_korzen()
    {
        // Przy synchronizacji zmiany przychodzą w kolejności zapisu, nie zależności,
        // więc podprojekt potrafi dotrzeć przed swoim celem. Ukrycie go znaczyłoby,
        // że zadanie istnieje, a nie widać go nigdzie.
        var obszar = Obszar("Urzędy", 1);
        var sierota = Projekt("Egzamin zdany", obszar.Id, rodzic: Guid.CreateVersion7());

        var wiersze = ProjectTree.Build([obszar], [sierota]);

        wiersze.Should().HaveCount(2);
        wiersze.Last().Label.Should().Be("Egzamin zdany");
        wiersze.Last().Depth.Should().Be(1);
    }

    [Fact]
    public void Cykl_nie_zapetla_budowania()
    {
        var obszar = Obszar("Dom", 1);
        var a = Projekt("A", obszar.Id);
        var b = Projekt("B", obszar.Id, rodzic: a.Id);
        a.AttachTo(b, Stamp());

        var buduj = () => ProjectTree.Build([obszar], [a, b]);

        buduj.Should().NotThrow();
    }

    [Fact]
    public void Projekt_pojawia_sie_tylko_raz()
    {
        var obszar = Obszar("Dom", 1);
        var cel = Projekt("Cel", obszar.Id);
        var krok = Projekt("Krok", obszar.Id, rodzic: cel.Id);

        var wiersze = ProjectTree.Build([obszar], [cel, krok]);

        wiersze.Select(w => w.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Obszary_ida_wedlug_wlasnej_kolejnosci()
    {
        var wiersze = ProjectTree.Build([Obszar("Trzeci", 3), Obszar("Pierwszy", 1), Obszar("Drugi", 2)], []);

        wiersze.Select(w => w.Label).Should().Equal("Pierwszy", "Drugi", "Trzeci");
    }
}
