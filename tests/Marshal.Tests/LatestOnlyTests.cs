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
        var queue = new LatestOnly();
        var jednoczesnie = 0;
        var szczyt = 0;

        var wpuszczenie = new TaskCompletionSource();

        async Task Work()
        {
            szczyt = Math.Max(szczyt, ++jednoczesnie);
            await wpuszczenie.Task;
            jednoczesnie--;
        }

        var first = queue.RunAsync(Work);
        var drugie = queue.RunAsync(Work);

        wpuszczenie.SetResult();
        await Task.WhenAll(first, drugie);

        szczyt.Should().Be(1);
    }

    [Fact]
    public async Task Zmiana_w_trakcie_przebiegu_powtarza_go_dokladnie_raz()
    {
        // Siedem znaków wpisanych w pole szukania ma dać drugi odczyt, nie siedem —
        // wyników pośrednich i tak nikt nie widzi.
        var queue = new LatestOnly();
        var przebiegi = 0;
        var wpuszczenie = new TaskCompletionSource();

        async Task Work()
        {
            przebiegi++;
            await wpuszczenie.Task;
        }

        var first = queue.RunAsync(Work);

        for (var i = 0; i < 6; i++)
        {
            _ = queue.RunAsync(Work);
        }

        wpuszczenie.SetResult();
        await first;

        przebiegi.Should().Be(2);
    }

    [Fact]
    public async Task Wolanie_w_trakcie_czeka_az_odswiezenie_naprawde_sie_wydarzy()
    {
        // Objaw, przez który to powstało: „await" na poleceniu odświeżenia kończył się,
        // zanim cokolwiek się odświeżyło, bo zgłoszenie w trakcie wracało natychmiast.
        // Dopóki odczyt z bazy kończył się bez oddania sterowania, przebieg i tak zdążył
        // przed następną linijką i nie dawało się tego zauważyć.
        var queue = new LatestOnly();
        var wpuszczenie = new TaskCompletionSource();
        var przebiegi = 0;

        async Task Work()
        {
            await wpuszczenie.Task;
            przebiegi++;
        }

        var first = queue.RunAsync(Work);
        var drugie = queue.RunAsync(Work);

        drugie.IsCompleted.Should().BeFalse("nic się jeszcze nie odświeżyło");

        wpuszczenie.SetResult();

        // Z ogranicznikiem czasu, bo pomyłka w tej klasie objawia się zawiśnięciem —
        // a test, który wisi, nie mówi nic poza tym, że przebieg trwa.
        await Task.WhenAll(first, drugie).WaitAsync(TimeSpan.FromSeconds(10));

        przebiegi.Should().Be(2, "drugie zgłoszenie ma doczekać się własnego przebiegu");
    }

    [Fact]
    public async Task Bledny_przebieg_nie_zatrzaskuje_kolejki()
    {
        var queue = new LatestOnly();

        var wybuch = async () => await queue.RunAsync(() => throw new InvalidOperationException());
        await wybuch.Should().ThrowAsync<InvalidOperationException>();

        var poszlo = false;
        await queue.RunAsync(() =>
        {
            poszlo = true;
            return Task.CompletedTask;
        });

        poszlo.Should().BeTrue();
    }
}
