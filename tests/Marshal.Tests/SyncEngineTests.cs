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
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private sealed class Urzadzenie : IDisposable
    {
        public Urzadzenie(string id, string katalog)
            : this(id, new LocalFolderTransport(katalog))
        {
        }

        public Urzadzenie(string id, ISyncTransport skladnica)
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
            Hlc = new HlcSource(Zegar, id);
            Engine = new SyncEngine(Db, skladnica, Hlc, id);
        }

        public string Id { get; }

        public Zegar Zegar { get; } = new();

        public SqliteConnection Connection { get; }

        public MarshalDbContext Db { get; }

        public HlcSource Hlc { get; }

        public SyncEngine Engine { get; }

        public TaskItem? Zadanie(Guid id) => Db.Tasks.AsNoTracking().FirstOrDefault(t => t.Id == id);

        public void Dispose()
        {
            Db.Dispose();
            Connection.Dispose();
        }
    }

    private readonly string _katalog =
        Path.Combine(Path.GetTempPath(), "marshal-sync-" + Guid.CreateVersion7().ToString("N")[..12]);

    private readonly Urzadzenie _biurko;
    private readonly Urzadzenie _telefon;
    private readonly Guid _obszar = Guid.CreateVersion7();

    public SyncEngineTests()
    {
        _biurko = new Urzadzenie("biurko", _katalog);
        _telefon = new Urzadzenie("telefon", _katalog);
    }

    private static Guid Dodaj(Urzadzenie u, string tytul)
    {
        var zadanie = TaskItem.Capture(tytul, u.Zegar.Now, u.Hlc.Next());
        u.Db.Tasks.Add(zadanie);
        u.Db.SaveChanges();
        return zadanie.Id;
    }

    [Fact]
    public async Task Zadanie_z_jednego_urzadzenia_pojawia_sie_na_drugim()
    {
        var id = Dodaj(_biurko, "Zadzwonić do przychodni");

        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _telefon.Zadanie(id)!.Title.Should().Be("Zadzwonić do przychodni");
        _telefon.Zadanie(id)!.State.Should().Be(TaskState.Inbox);
    }

    [Fact]
    public async Task Zmiany_roznych_pol_zrobione_rozlacznie_obie_przezywaja()
    {
        // To jest obietnica z 9.3 i główny powód, dla którego dziennik zapisuje pola,
        // a nie encje. Oba urządzenia pracują bez łączności, każde zmienia co innego.
        var id = Dodaj(_biurko, "Złożyć wniosek");
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

        foreach (var urzadzenie in new[] { _biurko, _telefon })
        {
            var zadanie = urzadzenie.Zadanie(id)!;
            zadanie.Title.Should().Be("Złożyć wniosek o wymianę");
            zadanie.Priority.Should().Be(Priority.High);
        }
    }

    [Fact]
    public async Task Przy_zmianie_tego_samego_pola_wygrywa_pozniejszy_znacznik()
    {
        var id = Dodaj(_biurko, "pierwotny");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _biurko.Db.Tasks.Single(t => t.Id == id).Rename("z biurka", _biurko.Hlc.Next());
        _biurko.Db.SaveChanges();

        // Telefon zmienia później — jego zegar idzie do przodu.
        _telefon.Zegar.Now = _telefon.Zegar.Now.AddMinutes(5);
        _telefon.Db.Tasks.Single(t => t.Id == id).Rename("z telefonu", _telefon.Hlc.Next());
        _telefon.Db.SaveChanges();

        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();
        await _biurko.Engine.SyncAsync();

        _biurko.Zadanie(id)!.Title.Should().Be("z telefonu");
        _telefon.Zadanie(id)!.Title.Should().Be("z telefonu");
    }

    [Fact]
    public async Task Scalanie_nie_tworzy_wlasnych_wpisow_w_dzienniku()
    {
        // Bez tego dwa urządzenia odbijałyby sobie te same zmiany bez końca,
        // przy czym każdy obieg z osobna wyglądałby na poprawny.
        Dodaj(_biurko, "cokolwiek");
        await _biurko.Engine.SyncAsync();

        await _telefon.Engine.SyncAsync();

        _telefon.Db.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Powtorna_synchronizacja_bez_zmian_nic_nie_robi()
    {
        Dodaj(_biurko, "cokolwiek");
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
        var id = Dodaj(_biurko, "Zadzwonić");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        foreach (var kursor in _telefon.Db.SyncCursors)
        {
            kursor.MoveTo(string.Empty);
        }

        _telefon.Db.SaveChanges();
        await _telefon.Engine.SyncAsync();

        _telefon.Db.Tasks.Count(t => t.Id == id).Should().Be(1);
        _telefon.Zadanie(id)!.Title.Should().Be("Zadzwonić");
    }

    [Fact]
    public async Task Kosz_dociera_jako_stan_a_nie_jako_zniknięcie()
    {
        var id = Dodaj(_biurko, "do wyrzucenia");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _biurko.Db.Tasks.Single(t => t.Id == id).Trash(_biurko.Hlc.Next());
        _biurko.Db.SaveChanges();

        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        _telefon.Zadanie(id)!.State.Should().Be(TaskState.Trashed);
    }

    [Fact]
    public async Task Popsuta_porcja_nie_psuje_pozostalych()
    {
        // Porcja z nowszej wersji aplikacji albo uszkodzona przez składnicę.
        // Ma zostać pominięta, a nie przerwać synchronizację — inaczej jedna zła
        // porcja unieruchomiłaby kursor i wszystko, co po niej.
        Dodaj(_biurko, "pierwsze");
        await _biurko.Engine.SyncAsync();

        var skladnica = new LocalFolderTransport(_katalog);
        await skladnica.WriteSegmentAsync("biurko", "000002", "{\"e\":\"Tasks\",\"id\":\n");

        Dodaj(_biurko, "trzecie");
        await _biurko.Engine.SyncAsync();

        await _telefon.Engine.SyncAsync();

        _telefon.Db.Tasks.Select(t => t.Title).Should().BeEquivalentTo(["pierwsze", "trzecie"]);
    }

    [Fact]
    public async Task Kolejne_wysylki_trafiaja_do_osobnych_porcji()
    {
        // Porcja raz zapisana się nie zmienia, więc druga wysyłka musi założyć nową.
        Dodaj(_biurko, "pierwsze");
        await _biurko.Engine.SyncAsync();
        Dodaj(_biurko, "drugie");
        await _biurko.Engine.SyncAsync();

        var porcje = await new LocalFolderTransport(_katalog).ListSegmentsAsync();

        porcje.Where(s => s.DeviceId == "biurko").Select(s => s.Name)
            .Should().Equal("000001", "000002");
    }

    [Fact]
    public async Task Porcja_pominieta_przy_pierwszym_czytaniu_dochodzi_przy_drugim()
    {
        // Kursor przesuwa się per porcja, więc porcja, która pojawiła się po
        // wylistowaniu, zostaje doczytana przy następnej synchronizacji.
        Dodaj(_biurko, "pierwsze");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        Dodaj(_biurko, "drugie");
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
        var dysk = new FakeDrive();
        using var biurko = new Urzadzenie("biurko", new GoogleDriveTransport(dysk));
        using var telefon = new Urzadzenie("telefon", new GoogleDriveTransport(dysk));

        var id = Dodaj(biurko, "Zadzwonić do przychodni");
        await biurko.Engine.SyncAsync();
        await telefon.Engine.SyncAsync();

        telefon.Zadanie(id)!.Title.Should().Be("Zadzwonić do przychodni");

        telefon.Db.Tasks.Single(t => t.Id == id).Rename("Umówić wizytę", telefon.Hlc.Next());
        telefon.Db.SaveChanges();
        await telefon.Engine.SyncAsync();
        await biurko.Engine.SyncAsync();

        biurko.Zadanie(id)!.Title.Should().Be("Umówić wizytę");
    }

    [Fact]
    public async Task Zegar_lokalny_podnosi_sie_ponad_zdalny()
    {
        _biurko.Zegar.Now = _biurko.Zegar.Now.AddHours(3);
        Dodaj(_biurko, "z przyszłości");
        await _biurko.Engine.SyncAsync();

        var przed = _telefon.Hlc.Last;
        await _telefon.Engine.SyncAsync();

        _telefon.Hlc.Next().Should().BeGreaterThan(przed);
        _telefon.Hlc.Last.WallMs.Should().BeGreaterThan(przed.WallMs);
    }

    [Fact]
    public async Task Wlasny_plik_nie_jest_czytany_przez_samego_siebie()
    {
        Dodaj(_biurko, "cokolwiek");

        var wynik = await _biurko.Engine.SyncAsync();

        wynik.Sent.Should().BeGreaterThan(0);
        wynik.Applied.Should().Be(0);
    }

    public void Dispose()
    {
        _biurko.Dispose();
        _telefon.Dispose();

        if (Directory.Exists(_katalog))
        {
            Directory.Delete(_katalog, recursive: true);
        }
    }
}
