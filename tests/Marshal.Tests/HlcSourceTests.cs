using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Infrastructure.Time;
using Xunit;

namespace Marshal.Tests;

public class HlcSourceTests
{
    private sealed class ZegarStojacy : IClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
    }

    [Fact]
    public void Kolejne_znaczniki_sa_scisle_rosnace_przy_stojacym_zegarze()
    {
        var zrodlo = new HlcSource(new ZegarStojacy(), "biurko");

        var znaczniki = Enumerable.Range(0, 100).Select(_ => zrodlo.Next()).ToArray();

        znaczniki.Should().BeInAscendingOrder();
        znaczniki.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Znaczniki_sa_unikalne_przy_zapisach_wspolbieznych()
    {
        var zrodlo = new HlcSource(new ZegarStojacy(), "biurko");
        var wynik = new System.Collections.Concurrent.ConcurrentBag<Hlc>();

        Parallel.For(0, 1000, _ => wynik.Add(zrodlo.Next()));

        wynik.Should().HaveCount(1000);
        wynik.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Observe_podnosi_zegar_ponad_znacznik_zdalny()
    {
        var zrodlo = new HlcSource(new ZegarStojacy(), "biurko");
        var zdalny = new Hlc(9_000_000, 3, "telefon");

        zrodlo.Observe(zdalny);

        zrodlo.Next().Should().BeGreaterThan(zdalny);
    }

    [Fact]
    public void Wznowienie_z_cudzego_urzadzenia_jest_odrzucane()
    {
        // Sprawdzenie przeniesione z konstruktora do pierwszego użycia: zegar powstaje
        // przy składaniu zależności, a tożsamość i wznowienie leżą w bazie, której
        // wtedy jeszcze nie ma. Odrzucenie nadal obowiązuje, tylko później.
        var zrodlo = new HlcSource(new ZegarStojacy(), "biurko", new Hlc(1, 0, "telefon"));

        var uzycie = () => zrodlo.Next();
        uzycie.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Utworzenie_zegara_nie_siega_po_tozsamosc()
    {
        // Warunek, żeby kolejność składania zależności i kolejność przygotowania bazy
        // nie musiały się o siebie opierać — zob. StartupTests.
        var siegnieto = false;

        _ = new HlcSource(
            new ZegarStojacy(),
            () => { siegnieto = true; return "biurko"; },
            () => { siegnieto = true; return null; });

        siegnieto.Should().BeFalse();
    }
}
