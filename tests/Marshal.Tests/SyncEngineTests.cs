using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Sync;
using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Sync.Google;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Dwa urządzenia, jedna wspólna składnica. Każde ma własną bazę i własny zegar,
/// tak jak w rzeczywistości.
/// </summary>
public sealed class SyncEngineTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private sealed class Device : IDisposable
    {
        public Device(string id, string folder)
            : this(id, new LocalFolderTransport(folder))
        {
        }

        public Device(string id, ISyncTransport store)
        {
            Id = id;
            Connection = new SqliteConnection("Filename=:memory:");
            Connection.Open();
            Db = new MarshalDbContext(
                new DbContextOptionsBuilder<MarshalDbContext>()
                    .UseSqlite(Connection)
                    .AddInterceptors(new ChangeJournalInterceptor())
                    .Options);
            Db.Database.Migrate();
            Hlc = new HlcSource(Clock, id);
            Engine = new SyncEngine(Db, store, Hlc, id);
        }

        public string Id { get; }

        public Clock Clock { get; } = new();

        public SqliteConnection Connection { get; }

        public MarshalDbContext Db { get; }

        public HlcSource Hlc { get; }

        public SyncEngine Engine { get; }

        public TaskItem? TaskId(Guid id) => Db.Tasks.AsNoTracking().FirstOrDefault(t => t.Id == id);

        public void Dispose()
        {
            Db.Dispose();
            Connection.Dispose();
        }
    }

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "marshal-sync-" + Guid.NewGuid().ToString("N"));

    private readonly Device _biurko;
    private readonly Device _telefon;
    private readonly Guid _area = Guid.CreateVersion7();

    public SyncEngineTests()
    {
        _biurko = new Device("biurko", _folder);
        _telefon = new Device("telefon", _folder);
    }

    private static Guid Add(Device u, string title)
    {
        var task = TaskItem.Capture(title, u.Clock.Now, u.Hlc.Next());
        u.Db.Tasks.Add(task);
        u.Db.SaveChanges();
        return task.Id;
    }

    [Fact]
    public async Task Zadanie_z_jednego_urzadzenia_pojawia_sie_na_drugim()
    {
        var id = Add(_biurko, "Zadzwonić do przychodni");

        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _telefon.TaskId(id)!.Title.Should().Be("Zadzwonić do przychodni");
        _telefon.TaskId(id)!.State.Should().Be(TaskState.Inbox);
    }

    [Fact]
    public async Task Zmiany_roznych_pol_zrobione_rozlacznie_obie_przezywaja()
    {
        // To jest obietnica z 9.3 i główny powód, dla którego dziennik zapisuje pola,
        // a nie encje. Oba urządzenia pracują bez łączności, każde zmienia co innego.
        var id = Add(_biurko, "Złożyć wniosek");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        var naBiurku = _biurko.Db.Tasks.Single(t => t.Id == id);
        naBiurku.Rename("Złożyć wniosek o wymianę", _biurko.Hlc.Next());
        _biurko.Db.SaveChanges();

        var naTelefonie = _telefon.Db.Tasks.Single(t => t.Id == id);
        naTelefonie.SetPriority(Priority.High, _telefon.Hlc.Next());
        _telefon.Db.SaveChanges();

        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();
        await _biurko.Engine.SyncAsync();

        foreach (var device in new[] { _biurko, _telefon })
        {
            var task = device.TaskId(id)!;
            task.Title.Should().Be("Złożyć wniosek o wymianę");
            task.Priority.Should().Be(Priority.High);
        }
    }

    [Fact]
    public async Task Przy_zmianie_tego_samego_pola_wygrywa_pozniejszy_znacznik()
    {
        var id = Add(_biurko, "pierwotny");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _biurko.Db.Tasks.Single(t => t.Id == id).Rename("z biurka", _biurko.Hlc.Next());
        _biurko.Db.SaveChanges();

        // Telefon zmienia później — jego zegar idzie do przodu.
        _telefon.Clock.Now = _telefon.Clock.Now.AddMinutes(5);
        _telefon.Db.Tasks.Single(t => t.Id == id).Rename("z telefonu", _telefon.Hlc.Next());
        _telefon.Db.SaveChanges();

        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();
        await _biurko.Engine.SyncAsync();

        _biurko.TaskId(id)!.Title.Should().Be("z telefonu");
        _telefon.TaskId(id)!.Title.Should().Be("z telefonu");
    }

    [Fact]
    public async Task Scalanie_nie_tworzy_wlasnych_wpisow_w_dzienniku()
    {
        // Bez tego dwa urządzenia odbijałyby sobie te same zmiany bez końca,
        // przy czym każdy obieg z osobna wyglądałby na poprawny.
        Add(_biurko, "cokolwiek");
        await _biurko.Engine.SyncAsync();

        await _telefon.Engine.SyncAsync();

        _telefon.Db.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Powtorna_synchronizacja_bez_zmian_nic_nie_robi()
    {
        Add(_biurko, "cokolwiek");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        var drugi = await _telefon.Engine.SyncAsync();

        drugi.Sent.Should().Be(0);
        drugi.Applied.Should().Be(0);
    }

    [Fact]
    public async Task Ponowne_zastosowanie_tych_samych_wpisow_nic_nie_zmienia()
    {
        // Kursor jest przyspieszeniem, nie warunkiem poprawności — po jego wyzerowaniu
        // porcje czyta się od początku i wynik musi być ten sam.
        var id = Add(_biurko, "Zadzwonić");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        foreach (var cursor in _telefon.Db.SyncCursors)
        {
            cursor.MoveTo(string.Empty);
        }

        _telefon.Db.SaveChanges();
        await _telefon.Engine.SyncAsync();

        _telefon.Db.Tasks.Count(t => t.Id == id).Should().Be(1);
        _telefon.TaskId(id)!.Title.Should().Be("Zadzwonić");
    }

    [Fact]
    public async Task Kosz_dociera_jako_stan_a_nie_jako_zniknięcie()
    {
        var id = Add(_biurko, "do wyrzucenia");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _biurko.Db.Tasks.Single(t => t.Id == id).Trash(_biurko.Hlc.Next());
        _biurko.Db.SaveChanges();

        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _telefon.TaskId(id)!.State.Should().Be(TaskState.Trashed);
    }

    [Fact]
    public async Task Popsuta_porcja_nie_psuje_pozostalych()
    {
        // Porcja z nowszej wersji aplikacji albo uszkodzona przez składnicę.
        // Ma zostać pominięta, a nie przerwać synchronizację — inaczej jedna zła
        // porcja unieruchomiłaby kursor i wszystko, co po niej.
        Add(_biurko, "pierwsze");
        await _biurko.Engine.SyncAsync();

        var store = new LocalFolderTransport(_folder);
        await store.WriteSegmentAsync("biurko", "000002", "{\"e\":\"Tasks\",\"id\":\n");

        Add(_biurko, "trzecie");
        await _biurko.Engine.SyncAsync();

        await _telefon.Engine.SyncAsync();

        _telefon.Db.Tasks.Select(t => t.Title).Should().BeEquivalentTo(["pierwsze", "trzecie"]);
    }

    [Fact]
    public async Task Kolejne_wysylki_trafiaja_do_osobnych_porcji()
    {
        // Porcja raz zapisana się nie zmienia, więc druga wysyłka musi założyć nową.
        Add(_biurko, "pierwsze");
        await _biurko.Engine.SyncAsync();
        Add(_biurko, "drugie");
        await _biurko.Engine.SyncAsync();

        var chunks = await new LocalFolderTransport(_folder).ListSegmentsAsync();

        chunks.Where(s => s.DeviceId == "biurko").Select(s => s.Name)
            .Should().Equal("000001", "000002");
    }

    [Fact]
    public async Task Porcja_pominieta_przy_pierwszym_czytaniu_dochodzi_przy_drugim()
    {
        // Kursor przesuwa się per porcja, więc porcja, która pojawiła się po
        // wylistowaniu, zostaje doczytana przy następnej synchronizacji.
        Add(_biurko, "pierwsze");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        Add(_biurko, "drugie");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _telefon.Db.Tasks.Select(t => t.Title).Should().BeEquivalentTo(["pierwsze", "drugie"]);
    }

    [Fact]
    public async Task Cala_droga_dziala_tak_samo_przez_skladnice_Dysku()
    {
        // Ten sam scenariusz co przez katalog, ale na kształcie, który narzuca Dysk:
        // płaskie nazwy plików zamiast katalogów na urządzenie. Jeśli scalanie zależy
        // od czegoś, co daje tylko system plików, to pęknie tutaj.
        var drive = new FakeDrive();
        using var biurko = new Device("biurko", new GoogleDriveTransport(drive));
        using var telefon = new Device("telefon", new GoogleDriveTransport(drive));

        var id = Add(biurko, "Zadzwonić do przychodni");
        await biurko.Engine.SyncAsync();
        await telefon.Engine.SyncAsync();

        telefon.TaskId(id)!.Title.Should().Be("Zadzwonić do przychodni");

        telefon.Db.Tasks.Single(t => t.Id == id).Rename("Umówić wizytę", telefon.Hlc.Next());
        telefon.Db.SaveChanges();
        await telefon.Engine.SyncAsync();
        await biurko.Engine.SyncAsync();

        biurko.TaskId(id)!.Title.Should().Be("Umówić wizytę");
    }

    [Fact]
    public async Task Zegar_lokalny_podnosi_sie_ponad_zdalny()
    {
        _biurko.Clock.Now = _biurko.Clock.Now.AddHours(3);
        Add(_biurko, "z przyszłości");
        await _biurko.Engine.SyncAsync();

        var before = _telefon.Hlc.Last;
        await _telefon.Engine.SyncAsync();

        _telefon.Hlc.Next().Should().BeGreaterThan(before);
        _telefon.Hlc.Last.WallMs.Should().BeGreaterThan(before.WallMs);
    }

    [Fact]
    public async Task Wlasny_plik_nie_jest_czytany_przez_samego_siebie()
    {
        Add(_biurko, "cokolwiek");

        var result = await _biurko.Engine.SyncAsync();

        result.Sent.Should().BeGreaterThan(0);
        result.Applied.Should().Be(0);
    }

    public void Dispose()
    {
        _biurko.Dispose();
        _telefon.Dispose();

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
