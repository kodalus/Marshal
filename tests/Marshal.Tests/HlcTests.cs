using FluentAssertions;
using Marshal.Domain.Primitives;
using Xunit;

namespace Marshal.Tests;

public class HlcTests
{
    [Fact]
    public void Next_wDrugiejZmianieWTejSamejMilisekundzie_zwiekszaLicznik()
    {
        var last = new Hlc(1000, 0, "a");

        var next = Hlc.Next(last, physicalNowMs: 1000);

        next.Should().Be(new Hlc(1000, 1, "a"));
    }

    [Fact]
    public void Next_wNowejMilisekundzie_zerujeLicznik()
    {
        var last = new Hlc(1000, 7, "a");

        var next = Hlc.Next(last, physicalNowMs: 1001);

        next.Should().Be(new Hlc(1001, 0, "a"));
    }

    [Fact]
    public void Next_gdyZegarSystemowySieCofnal_nieCofaZnacznika()
    {
        var last = new Hlc(1000, 0, "a");

        // Telefon przestawił zegar o pół sekundy wstecz.
        var next = Hlc.Next(last, physicalNowMs: 500);

        next.Should().BeGreaterThan(last);
        next.Should().Be(new Hlc(1000, 1, "a"));
    }

    [Fact]
    public void Next_wielokrotnie_jestScisleRosnacy()
    {
        var current = Hlc.Zero("a");
        var poprzedni = current;

        for (var i = 0; i < 1000; i++)
        {
            // Zegar stoi w miejscu przez cały przebieg — najgorszy przypadek.
            current = Hlc.Next(current, physicalNowMs: 42);
            current.Should().BeGreaterThan(poprzedni);
            poprzedni = current;
        }
    }

    [Fact]
    public void Merge_gdyCzasyRowne_bierzeWiekszyLicznikIDodajeJeden()
    {
        var last = new Hlc(1000, 5, "a");
        var remote = new Hlc(1000, 3, "b");

        var merged = Hlc.Merge(last, remote, physicalNowMs: 900);

        merged.Should().Be(new Hlc(1000, 6, "a"));
    }

    [Fact]
    public void Merge_gdyZdalnyZPrzyszlosci_podnosiZegarLokalny()
    {
        var last = new Hlc(1000, 5, "a");
        var remote = new Hlc(2000, 1, "b");

        var merged = Hlc.Merge(last, remote, physicalNowMs: 900);

        merged.Should().Be(new Hlc(2000, 2, "a"));
        merged.Should().BeGreaterThan(remote);
    }

    [Fact]
    public void Merge_gdyCzasFizycznyWyprzedzaOba_zerujeLicznik()
    {
        var last = new Hlc(1000, 5, "a");
        var remote = new Hlc(900, 9, "b");

        var merged = Hlc.Merge(last, remote, physicalNowMs: 3000);

        merged.Should().Be(new Hlc(3000, 0, "a"));
    }

    [Fact]
    public void Merge_zawszeZachowujeIdentyfikatorUrzadzeniaLokalnego()
    {
        var merged = Hlc.Merge(new Hlc(1, 0, "moje"), new Hlc(99, 9, "cudze"), physicalNowMs: 0);

        merged.DeviceId.Should().Be("moje");
    }

    [Fact]
    public void Porzadek_przyRownymCzasieILiczniku_rozstrzygaUrzadzenie()
    {
        var a = new Hlc(1000, 1, "aaa");
        var b = new Hlc(1000, 1, "bbb");

        a.Should().BeLessThan(b);
        b.Should().BeGreaterThan(a);
    }

    [Fact]
    public void Porzadek_jestDeterministycznyNiezaleznieOdKolejnosci()
    {
        var znaczniki = new[]
        {
            new Hlc(2, 0, "b"), new Hlc(1, 5, "a"), new Hlc(2, 0, "a"), new Hlc(1, 5, "b"),
        };

        var rosnaco = znaczniki.Order().ToArray();
        var odwrotnie = znaczniki.Reverse().Order().ToArray();

        rosnaco.Should().Equal(odwrotnie);
        rosnaco.Should().Equal(
            new Hlc(1, 5, "a"), new Hlc(1, 5, "b"), new Hlc(2, 0, "a"), new Hlc(2, 0, "b"));
    }

    [Fact]
    public void ToString_iParse_saWzajemnieOdwrotne()
    {
        var hlc = new Hlc(1757942400123, 7, "a3f1");

        hlc.ToString().Should().Be("1757942400123.7.a3f1");
        Hlc.Parse(hlc.ToString()).Should().Be(hlc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1757942400123")]
    [InlineData("1757942400123.7")]
    [InlineData(".7.a3f1")]
    [InlineData("1757942400123..a3f1")]
    [InlineData("1757942400123.7.")]
    [InlineData("-1.7.a3f1")]
    [InlineData("abc.7.a3f1")]
    [InlineData("1757942400123.x.a3f1")]
    public void TryParse_odrzucaNiepoprawnePostacie(string tekst)
    {
        Hlc.TryParse(tekst, out _).Should().BeFalse();
    }

    [Fact]
    public void Konstruktor_odrzucaKropkeWIdentyfikatorzeUrzadzenia()
    {
        var utworz = () => new Hlc(1, 0, "moje.urzadzenie");

        utworz.Should().Throw<ArgumentException>();
    }
}
