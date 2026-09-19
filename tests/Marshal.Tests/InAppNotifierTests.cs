using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Notifications;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Zaległość przypomnień pokazanych, zanim platforma podpięła dymki.
/// </summary>
/// <remarks>
/// <para>
/// To jest test usterki, przez którą przypomnienia na Androidzie milczały przy
/// zamkniętej aplikacji. Składanie zależności samo nadrabia zaległe przypomnienia,
/// a odbiornik budzika i widget wołają je w procesie <b>bez okna</b>. Pokazane wtedy
/// przypomnienie trafiało wyłącznie na listę do odebrania przez okno — a odbierać
/// nie miał kto. W bazie zostawało odnotowane jako pokazane, więc nie wracało już nigdy.
/// </para>
/// <para>
/// Z zewnątrz wyglądało to jak brak przypomnień, a w dzienniku jak „nie było czego
/// pokazać" — czyli najbardziej mylący możliwy komunikat.
/// </para>
/// </remarks>
[Collection("Powiadomienia systemowe")]
public sealed class InAppNotifierTests : IDisposable
{
    public void Dispose() => InAppNotifier.SystemSink = null;

    [Fact]
    public async Task Pokazane_przed_podpieciem_czeka_i_wychodzi_po_podpieciu()
    {
        // Jak wyżej: zerowanie zabiera to, co zostawiły inne klasy w tym samym procesie.
        InAppNotifier.SystemSink = null;

        var powiadamiacz = new InAppNotifier();
        var wyszly = new List<string>();

        await powiadamiacz.ShowAsync(new Notification(Guid.CreateVersion7(), "zanim", null));

        wyszly.Should().BeEmpty("haczyka jeszcze nie ma");

        InAppNotifier.SystemSink = (p, _) =>
        {
            lock (wyszly)
            {
                wyszly.Add(p.Title);
            }

            return Task.CompletedTask;
        };

        // Wypuszczanie zaległości jest oddzielone od podpięcia, żeby pokazanie dymka
        // nie działo się pod zamkiem — więc na jego skutek trzeba chwilę poczekać.
        await Poczekaj(() => wyszly.Count == 1);

        wyszly.Should().Equal("zanim");
    }

    [Fact]
    public async Task Po_podpieciu_idzie_od_razu_i_nie_dubluje()
    {
        // Zaległość jest statyczna, bo statyczny jest haczyk — a proces testów jest
        // jeden i inne klasy zdążyły już coś w niej zostawić. Odpięcie ją zapomina,
        // więc to zerowanie jest zarazem czyszczeniem stanowiska.
        InAppNotifier.SystemSink = null;

        var wyszly = new List<string>();

        InAppNotifier.SystemSink = (p, _) =>
        {
            lock (wyszly)
            {
                wyszly.Add(p.Title);
            }

            return Task.CompletedTask;
        };

        var powiadamiacz = new InAppNotifier();
        await powiadamiacz.ShowAsync(new Notification(Guid.CreateVersion7(), "po", null));

        wyszly.Should().Equal("po");

        // Powtórne podpięcie — na przykład drugie wejście do okna — nie ma wypuszczać
        // po raz drugi czegoś, co już wyszło.
        var tenSam = InAppNotifier.SystemSink;
        InAppNotifier.SystemSink = tenSam;
        await Poczekaj(() => false, TimeSpan.FromMilliseconds(120));

        wyszly.Should().Equal("po");
    }

    private static async Task Poczekaj(Func<bool> warunek, TimeSpan? count = null)
    {
        var end = DateTime.UtcNow + (count ?? TimeSpan.FromSeconds(2));

        while (DateTime.UtcNow < end && !warunek())
        {
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// Testy ruszające <see cref="InAppNotifier.Systemowe"/> idą jednym zbiorem, bo pole
/// jest statyczne: dwie klasy naraz podstawiałyby sobie nawzajem cudzy haczyk. Zbiór
/// xUnita nie zrównolegla swoich klas, więc sama przynależność wystarcza.
/// </summary>
[CollectionDefinition("Powiadomienia systemowe")]
public sealed class PowiadomieniaSystemoweCollection;
