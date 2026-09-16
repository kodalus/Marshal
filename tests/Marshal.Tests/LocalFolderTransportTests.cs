using FluentAssertions;
using Marshal.Application.Sync;
using Marshal.Infrastructure.Sync;
using Xunit;

namespace Marshal.Tests;

public sealed class LocalFolderTransportTests : IDisposable
{
    private readonly string _katalog =
        Path.Combine(Path.GetTempPath(), "marshal-testy-" + Guid.CreateVersion7().ToString("N")[..12]);

    private LocalFolderTransport Skladnica() => new(_katalog);

    [Fact]
    public async Task Pusta_skladnica_nie_ma_zadnych_porcji()
    {
        (await Skladnica().ListSegmentsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Zapisana_porcja_wraca_w_calosci()
    {
        var skladnica = Skladnica();

        await skladnica.WriteSegmentAsync("biurko", "000001", "pierwsza\ndruga\n");

        var porcje = await skladnica.ListSegmentsAsync();
        porcje.Should().ContainSingle();
        porcje[0].DeviceId.Should().Be("biurko");
        porcje[0].Name.Should().Be("000001");
        (await skladnica.ReadSegmentAsync(porcje[0])).Should().Be("pierwsza\ndruga\n");
    }

    [Fact]
    public async Task Polskie_litery_wracaja_niezmienione()
    {
        // Zapis i odczyt muszą chodzić tym samym kodowaniem. Rozjazd nie rzuca
        // wyjątku — daje zniekształcony tekst dopiero na drugim urządzeniu.
        var skladnica = Skladnica();

        await skladnica.WriteSegmentAsync("biurko", "000001", "zażółć gęślą jaźń\n");

        (await skladnica.ReadSegmentAsync(new LogSegment("biurko", "000001")))
            .Should().Be("zażółć gęślą jaźń\n");
    }

    [Fact]
    public async Task Zapisana_porcja_nie_daje_sie_nadpisac()
    {
        // Niezmienność porcji jest warunkiem poprawności kursora: skoro
        // „przeczytana zostaje przeczytana", to zmiana treści pod tą samą nazwą
        // przepadłaby na wszystkich urządzeniach, które ją już minęły.
        var skladnica = Skladnica();
        await skladnica.WriteSegmentAsync("biurko", "000001", "pierwotna\n");

        var ponownie = async () =>
            await skladnica.WriteSegmentAsync("biurko", "000001", "podmieniona\n");

        await ponownie.Should().ThrowAsync<InvalidOperationException>();
        (await skladnica.ReadSegmentAsync(new LogSegment("biurko", "000001")))
            .Should().Be("pierwotna\n");
    }

    [Fact]
    public async Task Porcje_wracaja_w_porzadku_nazw()
    {
        var skladnica = Skladnica();

        await skladnica.WriteSegmentAsync("biurko", "000010", "dziesiąta\n");
        await skladnica.WriteSegmentAsync("biurko", "000002", "druga\n");
        await skladnica.WriteSegmentAsync("biurko", "000001", "pierwsza\n");

        (await skladnica.ListSegmentsAsync()).Select(s => s.Name)
            .Should().ContainInOrder("000001", "000002", "000010");
    }

    [Fact]
    public async Task Odczyt_nieistniejacej_porcji_zwraca_pustke()
    {
        (await Skladnica().ReadSegmentAsync(new LogSegment("nieznane", "000001")))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Porcje_roznych_urzadzen_sa_rozlaczne()
    {
        var skladnica = Skladnica();

        await skladnica.WriteSegmentAsync("biurko", "000001", "z biurka\n");
        await skladnica.WriteSegmentAsync("telefon", "000001", "z telefonu\n");

        var porcje = await skladnica.ListSegmentsAsync();
        porcje.Should().HaveCount(2);
        (await skladnica.ReadSegmentAsync(new LogSegment("biurko", "000001")))
            .Should().Be("z biurka\n");
        (await skladnica.ReadSegmentAsync(new LogSegment("telefon", "000001")))
            .Should().Be("z telefonu\n");
    }

    [Fact]
    public async Task Plik_tymczasowy_przerwanego_zapisu_nie_jest_porcja()
    {
        // Przerwany zapis zostawia plik tymczasowy obok porcji. Gdyby trafił na
        // listę, drugie urządzenie przeczytałoby dziennik ucięty w pół wiersza.
        var skladnica = Skladnica();
        await skladnica.WriteSegmentAsync("biurko", "000001", "cała\n");
        await File.WriteAllTextAsync(
            Path.Combine(_katalog, "log", "biurko", "000002.jsonl.tmp"), "urwana");

        (await skladnica.ListSegmentsAsync()).Select(s => s.Name).Should().Equal("000001");
    }

    [Theory]
    [InlineData("../ucieczka")]
    [InlineData("a/b")]
    [InlineData("z kropką.")]
    [InlineData("")]
    public async Task Identyfikator_psujacy_nazwe_pliku_jest_odrzucany(string urzadzenie)
    {
        var zapisz = async () =>
            await Skladnica().WriteSegmentAsync(urzadzenie, "000001", "cokolwiek\n");

        await zapisz.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("../ucieczka")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Nazwa_porcji_psujaca_sciezke_jest_odrzucana(string porcja)
    {
        var zapisz = async () =>
            await Skladnica().WriteSegmentAsync("biurko", porcja, "cokolwiek\n");

        await zapisz.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_katalog))
        {
            Directory.Delete(_katalog, recursive: true);
        }
    }
}
