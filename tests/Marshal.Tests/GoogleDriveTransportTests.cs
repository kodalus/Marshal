using FluentAssertions;
using Marshal.Application.Sync;
using Marshal.Infrastructure.Sync.Google;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Dysk udawany w pamięci, z zachowaniem dwóch rzeczy, które prawdziwy Dysk robi,
/// a intuicja podpowiada inaczej: nazwy <b>nie są unikalne</b>, a katalogi
/// <b>zakładają się po nazwie, nie po tożsamości</b>.
/// </summary>
internal sealed class FakeDrive : IDriveClient
{
    private readonly Dictionary<string, string> _folders = [];
    private readonly List<(string Folder, DriveFile File, string Content)> _files = [];
    private int _next;

    public int Zapisow { get; private set; }

    public int SzukanKatalogu { get; private set; }

    public Task<string> EnsureFolderAsync(string name, CancellationToken ct = default)
    {
        SzukanKatalogu++;

        if (!_folders.TryGetValue(name, out var id))
        {
            id = "kat-" + (++_next);
            _folders[name] = id;
        }

        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<DriveFile>> ListAsync(
        string folderId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DriveFile>>(
            _files.Where(f => f.Folder == folderId).Select(f => f.File).ToList());

    public Task<string> DownloadAsync(string fileId, CancellationToken ct = default) =>
        Task.FromResult(_files.First(f => f.File.Id == fileId).Content);

    public Task CreateAsync(
        string folderId, string name, string content, CancellationToken ct = default)
    {
        Zapisow++;
        _files.Add((folderId, new DriveFile("plik-" + (++_next), name), content));
        return Task.CompletedTask;
    }

    /// <summary>Ponowiony zapis po zerwanym połączeniu: ta sama nazwa, drugi plik.</summary>
    public void Zdubluj(string name)
    {
        var oryginal = _files.First(f => f.File.Name == name);
        _files.Add((oryginal.Folder, new DriveFile("plik-" + (++_next), name), oryginal.Content));
    }

    public void PodrzucSmiec(string name) =>
        _files.Add((_folders.Values.First(), new DriveFile("śmieć", name), "nieważne"));
}

public sealed class GoogleDriveTransportTests
{
    private readonly FakeDrive _drive = new();

    private GoogleDriveTransport NewStore() => new(_drive);

    [Fact]
    public async Task Pusty_dysk_nie_ma_zadnych_porcji()
    {
        (await NewStore().ListSegmentsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Zapisana_porcja_wraca_w_calosci()
    {
        var store = NewStore();

        await store.WriteSegmentAsync("biurko", "000001", "pierwsza\ndruga\n");

        var chunks = await store.ListSegmentsAsync();
        chunks.Should().ContainSingle();
        chunks[0].Should().Be(new LogSegment("biurko", "000001"));
        (await store.ReadSegmentAsync(chunks[0])).Should().Be("pierwsza\ndruga\n");
    }

    [Fact]
    public async Task Urzadzenie_wyczytuje_sie_z_nazwy_pliku()
    {
        // Płaskie nazewnictwo to jedyne, co odróżnia urządzenia na Dysku — katalogów
        // na urządzenie nie ma celowo, bo nazwy katalogów nie są unikalne.
        var store = NewStore();

        await store.WriteSegmentAsync("biurko", "000001", "z biurka\n");
        await store.WriteSegmentAsync("telefon", "000001", "z telefonu\n");

        (await store.ListSegmentsAsync()).Should().Equal(
            new LogSegment("biurko", "000001"),
            new LogSegment("telefon", "000001"));
        (await store.ReadSegmentAsync(new LogSegment("telefon", "000001")))
            .Should().Be("z telefonu\n");
    }

    [Fact]
    public async Task Porcje_wracaja_w_porzadku_nazw()
    {
        var store = NewStore();

        await store.WriteSegmentAsync("biurko", "000010", "dziesiąta\n");
        await store.WriteSegmentAsync("biurko", "000002", "druga\n");
        await store.WriteSegmentAsync("biurko", "000001", "pierwsza\n");

        (await store.ListSegmentsAsync()).Select(s => s.Name)
            .Should().ContainInOrder("000001", "000002", "000010");
    }

    [Fact]
    public async Task Zdublowany_plik_liczy_sie_raz()
    {
        // Ponowienie zapisu po zerwanym połączeniu zostawia na Dysku dwa pliki o tej
        // samej nazwie. Treść jest ta sama, więc porcja ma się pojawić na liście raz.
        var store = NewStore();
        await store.WriteSegmentAsync("biurko", "000001", "treść\n");
        _drive.Zdubluj("biurko.000001.jsonl");

        (await store.ListSegmentsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Obcy_plik_w_katalogu_nie_jest_porcja()
    {
        // Katalog roboczy może dostać cokolwiek — choćby notatkę wrzuconą ręcznie.
        var store = NewStore();
        await store.WriteSegmentAsync("biurko", "000001", "treść\n");
        _drive.PodrzucSmiec("notatka.txt");
        _drive.PodrzucSmiec("bez-kropki.jsonl");
        _drive.PodrzucSmiec("za.duzo.kropek.jsonl");

        (await store.ListSegmentsAsync()).Should().Equal(new LogSegment("biurko", "000001"));
    }

    [Fact]
    public async Task Zapisana_porcja_nie_daje_sie_nadpisac()
    {
        var store = NewStore();
        await store.WriteSegmentAsync("biurko", "000001", "pierwotna\n");

        var again = async () =>
            await store.WriteSegmentAsync("biurko", "000001", "podmieniona\n");

        await again.Should().ThrowAsync<InvalidOperationException>();
        _drive.Zapisow.Should().Be(1);
    }

    [Fact]
    public async Task Odczyt_nieistniejacej_porcji_zwraca_pustke()
    {
        (await NewStore().ReadSegmentAsync(new LogSegment("nieznane", "000001")))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Katalog_roboczy_zakladany_jest_raz_na_wiele_operacji()
    {
        // Każde szukanie katalogu to osobne odpytanie sieci. Przy synchronizacji
        // wołanej co kilka minut na telefonie to nie jest kosmetyka.
        var store = NewStore();

        await store.WriteSegmentAsync("biurko", "000001", "a\n");
        await store.ListSegmentsAsync();
        await store.ReadSegmentAsync(new LogSegment("biurko", "000001"));

        _drive.SzukanKatalogu.Should().Be(1);
    }

    [Theory]
    [InlineData("../ucieczka")]
    [InlineData("a/b")]
    [InlineData("z kropką.")]
    [InlineData("kropka.w.srodku")]
    [InlineData("")]
    public async Task Identyfikator_psujacy_nazwe_jest_odrzucany(string device)
    {
        // Kropka rozdziela człony nazwy, więc kropka w identyfikatorze rozsypałaby
        // odczyt: „a.b.000001.jsonl" przeczytałoby się jako urządzenie „a".
        var save = async () =>
            await NewStore().WriteSegmentAsync(device, "000001", "cokolwiek\n");

        await save.Should().ThrowAsync<ArgumentException>();
    }
}
