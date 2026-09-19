using FluentAssertions;
using Marshal.Application.Sync;
using Marshal.Infrastructure.Sync;
using Xunit;

namespace Marshal.Tests;

public sealed class LocalFolderTransportTests : IDisposable
{
    private readonly string _katalog =
        Path.Combine(Path.GetTempPath(), "marshal-testy-" + Guid.NewGuid().ToString("N"));

    private LocalFolderTransport Skladnica() => new(_katalog);

    [Fact]
    public async Task Pusta_skladnica_nie_ma_zadnych_porcji()
    {
        (await Skladnica().ListSegmentsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Zapisana_porcja_wraca_w_calosci()
    {
        var store = Skladnica();

        await store.WriteSegmentAsync("biurko", "000001", "pierwsza\ndruga\n");

        var chunks = await store.ListSegmentsAsync();
        chunks.Should().ContainSingle();
        chunks[0].DeviceId.Should().Be("biurko");
        chunks[0].Name.Should().Be("000001");
        (await store.ReadSegmentAsync(chunks[0])).Should().Be("pierwsza\ndruga\n");
    }

    [Fact]
    public async Task Polskie_litery_wracaja_niezmienione()
    {
        // Zapis i odczyt muszą chodzić tym samym kodowaniem. Rozjazd nie rzuca
        // wyjątku — daje zniekształcony tekst dopiero na drugim urządzeniu.
        var store = Skladnica();

        await store.WriteSegmentAsync("biurko", "000001", "zażółć gęślą jaźń\n");

        (await store.ReadSegmentAsync(new LogSegment("biurko", "000001")))
            .Should().Be("zażółć gęślą jaźń\n");
    }

    [Fact]
    public async Task Zapisana_porcja_nie_daje_sie_nadpisac()
    {
        // Niezmienność porcji jest warunkiem poprawności kursora: skoro
        // „przeczytana zostaje przeczytana", to zmiana treści pod tą samą nazwą
        // przepadłaby na wszystkich urządzeniach, które ją już minęły.
        var store = Skladnica();
        await store.WriteSegmentAsync("biurko", "000001", "pierwotna\n");

        var again = async () =>
            await store.WriteSegmentAsync("biurko", "000001", "podmieniona\n");

        await again.Should().ThrowAsync<InvalidOperationException>();
        (await store.ReadSegmentAsync(new LogSegment("biurko", "000001")))
            .Should().Be("pierwotna\n");
    }

    [Fact]
    public async Task Porcje_wracaja_w_porzadku_nazw()
    {
        var store = Skladnica();

        await store.WriteSegmentAsync("biurko", "000010", "dziesiąta\n");
        await store.WriteSegmentAsync("biurko", "000002", "druga\n");
        await store.WriteSegmentAsync("biurko", "000001", "pierwsza\n");

        (await store.ListSegmentsAsync()).Select(s => s.Name)
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
        var store = Skladnica();

        await store.WriteSegmentAsync("biurko", "000001", "z biurka\n");
        await store.WriteSegmentAsync("telefon", "000001", "z telefonu\n");

        var chunks = await store.ListSegmentsAsync();
        chunks.Should().HaveCount(2);
        (await store.ReadSegmentAsync(new LogSegment("biurko", "000001")))
            .Should().Be("z biurka\n");
        (await store.ReadSegmentAsync(new LogSegment("telefon", "000001")))
            .Should().Be("z telefonu\n");
    }

    [Fact]
    public async Task Plik_tymczasowy_przerwanego_zapisu_nie_jest_porcja()
    {
        // Przerwany zapis zostawia plik tymczasowy obok porcji. Gdyby trafił na
        // listę, drugie urządzenie przeczytałoby dziennik ucięty w pół wiersza.
        var store = Skladnica();
        await store.WriteSegmentAsync("biurko", "000001", "cała\n");
        await File.WriteAllTextAsync(
            Path.Combine(_katalog, "log", "biurko", "000002.jsonl.tmp"), "urwana");

        (await store.ListSegmentsAsync()).Select(s => s.Name).Should().Equal("000001");
    }

    [Theory]
    [InlineData("../ucieczka")]
    [InlineData("a/b")]
    [InlineData("z kropką.")]
    [InlineData("")]
    public async Task Identyfikator_psujacy_nazwe_pliku_jest_odrzucany(string device)
    {
        var save = async () =>
            await Skladnica().WriteSegmentAsync(device, "000001", "cokolwiek\n");

        await save.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("../ucieczka")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Nazwa_porcji_psujaca_sciezke_jest_odrzucana(string porcja)
    {
        var save = async () =>
            await Skladnica().WriteSegmentAsync("biurko", porcja, "cokolwiek\n");

        await save.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_katalog))
        {
            Directory.Delete(_katalog, recursive: true);
        }
    }
}
