using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure;
using Marshal.UI;
using Marshal.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Bramka przed przejściem dnia: kiedy wolno już sądzić o wczorajszym dniu.
/// </summary>
/// <remarks>
/// <para>
/// Jedna linijka warunku, a traciła dni. Bramka ma się otworzyć wtedy, gdy <b>nie ma
/// na co czekać</b> — czyli gdy to urządzenie jest jedynym źródłem prawdy o wczoraj.
/// Tak jest przy braku poświadczeń: nikt nie zsynchronizuje się z nikim. Warunek
/// obejmował jednak także brak żetonu, a to zupełnie inna sytuacja: drugie urządzenie
/// istnieje i ma wiedzę, której tu nie ma.
/// </para>
/// <para>
/// Objaw: wystąpienie serii odhaczone wieczorem na telefonie znikało na pulpicie
/// z wczorajszego dnia. Lokalnie wyglądało na przegapione, dzisiejsze już stało, więc
/// przejście dnia uznawało je za przepadłe i stawiało nagrobek. Nagrobek na dniu
/// minionym jest nieodwracalny — okno serii nigdy nie planuje dni przed dzisiejszym —
/// a prostowanie odhaczonych go nie ratuje, bo to urządzenie nie wie, że cokolwiek
/// zostało odhaczone.
/// </para>
/// <para>
/// I nie była to droga rzadka. Zgoda Google w trybie testowym wygasa po siedmiu dniach,
/// a wygasły żeton jest wtedy kasowany — czyli ten warunek stawał się prawdziwy
/// <b>co tydzień</b>.
/// </para>
/// </remarks>
public sealed class SyncGateTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "marshal-bramka-" + Guid.NewGuid().ToString("N"));

    private async Task<ServiceProvider> OpenAsync()
    {
        Directory.CreateDirectory(_folder);

        var services = new ServiceCollection()
            .AddMarshal(Path.Combine(_folder, "marshal.db"))
            .AddMarshalViewModels()
            .BuildServiceProvider();

        await DependencyInjection.PrepareAsync(services);

        return services;
    }

    [Fact]
    public async Task Bez_poswiadczen_bramka_otwiera_sie_od_razu()
    {
        // Nie ma drugiego urządzenia, więc nie ma na co czekać: to urządzenie jest
        // jedynym źródłem prawdy o wczorajszym dniu.
        using var services = await OpenAsync();

        services.GetRequiredService<SyncState>().Settled.Should().BeFalse("zanim cokolwiek ruszy");

        await services.GetRequiredService<MainViewModel>().SyncAfterReturnAsync();

        services.GetRequiredService<SyncState>().Settled.Should().BeTrue();
    }

    [Fact]
    public async Task Z_poswiadczeniami_ale_bez_zetonu_bramka_zostaje_zamknieta()
    {
        // To jest cała poprawka. Poświadczenia wpisane znaczą, że synchronizacja jest
        // ustawiona i że drugie urządzenie gdzieś jest; brak żetonu znaczy tylko, że
        // właśnie teraz nie da się do niego dosięgnąć. Przejście dnia nie ma wtedy
        // prawa sądzić o wczoraj — bo sądzi nieodwracalnie, a danych nie ma od dni.
        using var services = await OpenAsync();

        services.GetRequiredService<ISettings>()
            .SetGoogle("123.apps.googleusercontent.com", "GOCSPX-cokolwiek");

        await services.GetRequiredService<MainViewModel>().SyncAfterReturnAsync();

        services.GetRequiredService<SyncState>().Settled.Should().BeFalse(
            "zgoda wygasa co tydzień, a wygasły żeton jest kasowany — "
            + "to nie znaczy, że drugie urządzenie przestało istnieć");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Plik bazy bywa jeszcze trzymany przez sterownik. Nieposprzątany katalog
            // tymczasowy nie jest wynikiem testu i nie ma go przewracać.
        }
    }
}
