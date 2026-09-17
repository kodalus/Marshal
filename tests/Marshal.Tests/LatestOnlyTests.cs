using FluentAssertions;
using Marshal.Application;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Odświeżanie sterowane zmianą pola: jeden przebieg naraz, na końcu zawsze najnowszy.
/// </summary>
public sealed class LatestOnlyTests
{
    [Fact]
    public async Task Drugie_wolanie_w_trakcie_pierwszego_nie_wchodzi_rownolegle()
    {
        // To jest cały powód istnienia tej klasy: dwa odczyty naraz na jednym
        // kontekście bazy kończą się wyjątkiem, którego nie ma kto złapać.
        var kolejka = new LatestOnly();
        var jednoczesnie = 0;
        var szczyt = 0;

        var wpuszczenie = new TaskCompletionSource();

        async Task Praca()
        {
            szczyt = Math.Max(szczyt, ++jednoczesnie);
            await wpuszczenie.Task;
            jednoczesnie--;
        }

        var pierwsze = kolejka.RunAsync(Praca);
        var drugie = kolejka.RunAsync(Praca);

        wpuszczenie.SetResult();
        await Task.WhenAll(pierwsze, drugie);

        szczyt.Should().Be(1);
    }

    [Fact]
    public async Task Zmiana_w_trakcie_przebiegu_powtarza_go_dokladnie_raz()
    {
        // Siedem znaków wpisanych w pole szukania ma dać drugi odczyt, nie siedem —
        // wyników pośrednich i tak nikt nie widzi.
        var kolejka = new LatestOnly();
        var przebiegi = 0;
        var wpuszczenie = new TaskCompletionSource();

        async Task Praca()
        {
            przebiegi++;
            await wpuszczenie.Task;
        }

        var pierwsze = kolejka.RunAsync(Praca);

        for (var i = 0; i < 6; i++)
        {
            _ = kolejka.RunAsync(Praca);
        }

        wpuszczenie.SetResult();
        await pierwsze;

        przebiegi.Should().Be(2);
    }

    [Fact]
    public async Task Bledny_przebieg_nie_zatrzaskuje_kolejki()
    {
        var kolejka = new LatestOnly();

        var wybuch = async () => await kolejka.RunAsync(() => throw new InvalidOperationException());
        await wybuch.Should().ThrowAsync<InvalidOperationException>();

        var poszlo = false;
        await kolejka.RunAsync(() =>
        {
            poszlo = true;
            return Task.CompletedTask;
        });

        poszlo.Should().BeTrue();
    }
}
