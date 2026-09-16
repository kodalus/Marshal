using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
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
            Engine = new SyncEngine(Db, new LocalFolderTransport(katalog), Hlc, id);
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
        // plik czyta się od początku i wynik musi być ten sam.
        var id = Dodaj(_biurko, "Zadzwonić");
        await _biurko.Engine.SyncAsync();
        await _telefon.Engine.SyncAsync();

        foreach (var kursor in _telefon.Db.SyncCursors)
        {
            kursor.MoveTo(0);
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
    public async Task Urwany_ogon_pliku_nie_psuje_scalania()
    {
        Dodaj(_biurko, "pierwsze");
        await _biurko.Engine.SyncAsync();

        // Drugie urządzenie zapisywało w trakcie naszego czytania.
        await new LocalFolderTransport(_katalog).AppendAsync("biurko", "{\"e\":\"Tasks\",\"id\":");

        var wynik = await _telefon.Engine.SyncAsync();

        wynik.Applied.Should().BeGreaterThan(0);
        _telefon.Db.Tasks.Should().ContainSingle();
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
