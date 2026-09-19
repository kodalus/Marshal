using System.Text;
using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Załączniki adresowane treścią (spec 9.2).
/// </summary>
public sealed class AttachmentServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "marshal-pliki-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly HlcSource _hlc;
    private readonly LocalFolderFileTransport _store;
    private readonly AttachmentService _service;
    private readonly Guid _task = Guid.CreateVersion7();

    public AttachmentServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_clock, "biurko");
        _store = new LocalFolderFileTransport(_folder);

        _service = new AttachmentService(
            new AttachmentRepository(_db), _store, new UnitOfWork(_db), _clock, _hlc);
    }

    private static MemoryStream FileName(string content) => new(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Dodany_plik_da_sie_otworzyc()
    {
        var attachment = await _service.AddAsync(FileName("zawartość"), "notatka.txt", taskId: _task);

        await using var stream = await _service.OpenAsync(attachment);
        stream.Should().NotBeNull();

        using var reader = new StreamReader(stream!);
        (await reader.ReadToEndAsync()).Should().Be("zawartość");
    }

    [Fact]
    public async Task Skrot_jest_adresem_i_wyglada_jak_skrot()
    {
        var attachment = await _service.AddAsync(FileName("cokolwiek"), "a.txt", taskId: _task);

        attachment.Sha256.Should().HaveLength(64);
        attachment.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Ten_sam_plik_dwa_razy_zajmuje_jedno_miejsce()
    {
        // Adresem jest skrót treści, więc dwa takie same zdjęcia to jeden plik.
        // Wpisy zostają dwa, bo „podpięłam to tutaj" i „tam" to dwie decyzje.
        var first = await _service.AddAsync(FileName("to samo"), "a.txt", taskId: _task);
        var drugi = await _service.AddAsync(FileName("to samo"), "b.txt", noteId: Guid.CreateVersion7());

        first.Sha256.Should().Be(drugi.Sha256);
        first.Id.Should().NotBe(drugi.Id);
        Directory.GetFiles(Path.Combine(_folder, "files")).Should().ContainSingle();
    }

    [Fact]
    public async Task Rozne_pliki_maja_rozne_skroty()
    {
        var first = await _service.AddAsync(FileName("jedno"), "a.txt", taskId: _task);
        var drugi = await _service.AddAsync(FileName("drugie"), "b.txt", taskId: _task);

        first.Sha256.Should().NotBe(drugi.Sha256);
    }

    [Fact]
    public async Task Nazwa_pliku_jest_zapamietana_bez_sciezki()
    {
        // Ścieżka z jednego urządzenia nie znaczy nic na drugim, a bywa, że zdradza
        // więcej, niż trzeba.
        var attachment = await _service.AddAsync(
            FileName("x"), "/home/kto/Dokumenty/skan.pdf", taskId: _task);

        attachment.FileName.Should().Be("skan.pdf");
    }

    [Fact]
    public async Task Zalacznik_bez_treci_zwraca_pustke_a_nie_wyjatek()
    {
        // Wpis wędruje dziennikiem, treść osobną drogą — więc na drugim urządzeniu wpis
        // potrafi być przed plikiem. To stan normalny, nie awaria.
        var attachment = await _service.AddAsync(FileName("treść"), "a.txt", taskId: _task);
        File.Delete(Path.Combine(_folder, "files", attachment.Sha256));

        (await _service.OpenAsync(attachment)).Should().BeNull();
    }

    [Fact]
    public async Task Zalaczniki_wracaja_dla_swojego_zadania()
    {
        var inne = Guid.CreateVersion7();
        await _service.AddAsync(FileName("a"), "a.txt", taskId: _task);
        await _service.AddAsync(FileName("b"), "b.txt", taskId: _task);
        await _service.AddAsync(FileName("c"), "c.txt", taskId: inne);

        (await _service.ForTaskAsync(_task)).Should().HaveCount(2);
        (await _service.ForTaskAsync(inne)).Should().ContainSingle();
    }

    [Fact]
    public async Task Usuniety_zalacznik_znika_z_listy_a_tresc_zostaje()
    {
        // Dwa wpisy mogą wskazywać ten sam plik; liczenie, który był ostatni, kosztowałoby
        // przejście po całej bazie przy każdym usunięciu.
        var attachment = await _service.AddAsync(FileName("treść"), "a.txt", taskId: _task);

        await _service.RemoveAsync(attachment.Id);

        (await _service.ForTaskAsync(_task)).Should().BeEmpty();
        (await _store.ExistsAsync(attachment.Sha256)).Should().BeTrue();
    }

    [Fact]
    public async Task Zalacznik_musi_byc_do_czegos_podpiety()
    {
        var add = async () => await _service.AddAsync(FileName("x"), "a.txt");

        await add.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("krótki")]
    [InlineData("ZDUZYCH1234567890123456789012345678901234567890123456789012345678")]
    [InlineData("../../ucieczka")]
    public async Task Skladnica_odrzuca_adres_ktory_nie_jest_skrotem(string address)
    {
        var open = async () => await _store.ExistsAsync(address);

        await open.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Powtorne_wgranie_tej_samej_tresci_nie_psuje_pliku()
    {
        var attachment = await _service.AddAsync(FileName("treść"), "a.txt", taskId: _task);
        await _store.PutAsync(attachment.Sha256, FileName("treść"));

        await using var stream = await _store.OpenAsync(attachment.Sha256);
        using var reader = new StreamReader(stream!);
        (await reader.ReadToEndAsync()).Should().Be("treść");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
