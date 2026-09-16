using System.Text;
using FluentAssertions;
using Marshal.Infrastructure.Sync;
using Xunit;

namespace Marshal.Tests;

public sealed class LocalFolderTransportTests : IDisposable
{
    private readonly string _katalog =
        Path.Combine(Path.GetTempPath(), "marshal-testy-" + Guid.CreateVersion7().ToString("N")[..12]);

    private LocalFolderTransport Skladnica() => new(_katalog);

    [Fact]
    public async Task Pusta_skladnica_nie_ma_zadnych_plikow()
    {
        (await Skladnica().ListLogsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Dopisanie_tworzy_plik_i_zwieksza_dlugosc()
    {
        var skladnica = Skladnica();

        await skladnica.AppendAsync("biurko", "pierwsza\n");
        await skladnica.AppendAsync("biurko", "druga\n");

        var logi = await skladnica.ListLogsAsync();
        logi.Should().ContainSingle();
        logi[0].DeviceId.Should().Be("biurko");
        logi[0].Length.Should().Be(Encoding.UTF8.GetByteCount("pierwsza\ndruga\n"));
    }

    [Fact]
    public async Task Odczyt_od_przesuniecia_zwraca_wylacznie_nowa_tresc()
    {
        var skladnica = Skladnica();
        await skladnica.AppendAsync("biurko", "pierwsza\n");
        var po = (await skladnica.ListLogsAsync())[0].Length;
        await skladnica.AppendAsync("biurko", "druga\n");

        (await skladnica.ReadFromAsync("biurko", po)).Should().Be("druga\n");
    }

    [Fact]
    public async Task Przesuniecie_liczone_jest_w_bajtach_nie_w_znakach()
    {
        // Polska litera zajmuje dwa bajty w UTF-8. Przesunięcie liczone w znakach
        // rozjechałoby się o tyle bajtów, ile liter spoza ASCII przeszło wcześniej.
        var skladnica = Skladnica();
        await skladnica.AppendAsync("biurko", "zażółć\n");
        var po = (await skladnica.ListLogsAsync())[0].Length;
        await skladnica.AppendAsync("biurko", "gęślą\n");

        po.Should().Be(Encoding.UTF8.GetByteCount("zażółć\n"));
        (await skladnica.ReadFromAsync("biurko", po)).Should().Be("gęślą\n");
    }

    [Fact]
    public async Task Odczyt_poza_koncem_pliku_zwraca_pustke()
    {
        var skladnica = Skladnica();
        await skladnica.AppendAsync("biurko", "cokolwiek\n");

        (await skladnica.ReadFromAsync("biurko", 10_000)).Should().BeEmpty();
    }

    [Fact]
    public async Task Odczyt_nieistniejacego_urzadzenia_zwraca_pustke()
    {
        (await Skladnica().ReadFromAsync("nieznane", 0)).Should().BeEmpty();
    }

    [Fact]
    public async Task Pliki_roznych_urzadzen_sa_rozlaczne()
    {
        var skladnica = Skladnica();

        await skladnica.AppendAsync("biurko", "z biurka\n");
        await skladnica.AppendAsync("telefon", "z telefonu\n");

        (await skladnica.ReadFromAsync("biurko", 0)).Should().Be("z biurka\n");
        (await skladnica.ReadFromAsync("telefon", 0)).Should().Be("z telefonu\n");
        (await skladnica.ListLogsAsync()).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("../ucieczka")]
    [InlineData("a/b")]
    [InlineData("z kropką.")]
    [InlineData("")]
    public async Task Identyfikator_psujacy_nazwe_pliku_jest_odrzucany(string urzadzenie)
    {
        var dopisz = async () => await Skladnica().AppendAsync(urzadzenie, "cokolwiek\n");

        await dopisz.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_katalog))
        {
            Directory.Delete(_katalog, recursive: true);
        }
    }
}
