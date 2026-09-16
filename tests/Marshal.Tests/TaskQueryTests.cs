using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>Zapytania stojące za ekranami Dzisiaj, Plany i Archiwum.</summary>
public sealed class TaskQueryTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private static readonly DateOnly Dzis = new(2026, 9, 16);
    private readonly SqliteConnection _connection;
    private readonly MarshalDbContext _db;
    private readonly TaskRepository _zadania;
    private readonly Guid _obszar = Guid.CreateVersion7();
    private long _znacznik = 1000;

    public TaskQueryTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _zadania = new TaskRepository(_db);
    }

    private Hlc Stamp() => new(_znacznik += 10, 0, "t");

    private TaskItem Dodaj(string tytul, Action<TaskItem>? ustaw = null)
    {
        var zadanie = TaskItem.Capture(tytul, new Zegar().Now, Stamp());
        zadanie.MakeNext(_obszar, Stamp());
        ustaw?.Invoke(zadanie);
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();
        return zadanie;
    }

    [Fact]
    public async Task Dzisiaj_bierze_zadania_z_dniem_wykonania_dzis_i_wczesniej()
    {
        Dodaj("na dziś", t => t.Schedule(_obszar, Dzis, Stamp()));
        Dodaj("zaległe", t => t.Schedule(_obszar, Dzis.AddDays(-3), Stamp()));
        Dodaj("na jutro", t => t.Schedule(_obszar, Dzis.AddDays(1), Stamp()));

        var wynik = await _zadania.TodayAsync(Dzis);

        wynik.Select(t => t.Title).Should().BeEquivalentTo("na dziś", "zaległe");
    }

    [Fact]
    public async Task Dzisiaj_bierze_takze_zadania_po_terminie()
    {
        Dodaj("termin minął", t => t.SetDeadline(Dzis.AddDays(-1), Stamp()));

        (await _zadania.TodayAsync(Dzis)).Should().ContainSingle();
    }

    [Fact]
    public async Task Dzisiaj_stawia_terminy_przed_planami()
    {
        Dodaj("plan na dziś", t => t.Schedule(_obszar, Dzis, Stamp()));
        Dodaj("termin dziś", t => t.SetDeadline(Dzis, Stamp()));

        var wynik = await _zadania.TodayAsync(Dzis);

        wynik.First().Title.Should().Be("termin dziś");
    }

    [Fact]
    public async Task Dzisiaj_pomija_skrzynke_kosz_i_wykonane()
    {
        var wrzut = TaskItem.Capture("w skrzynce", new Zegar().Now, Stamp());
        _db.Tasks.Add(wrzut);

        Dodaj("wyrzucone", t => { t.Schedule(_obszar, Dzis, Stamp()); t.Trash(Stamp()); });
        Dodaj("zrobione", t => { t.Schedule(_obszar, Dzis, Stamp()); t.Complete(new Zegar().Now, Stamp()); });
        _db.SaveChanges();

        (await _zadania.TodayAsync(Dzis)).Should().BeEmpty();
    }

    [Fact]
    public async Task Plany_biora_wylacznie_przyszlosc_w_zadanym_oknie()
    {
        Dodaj("dziś", t => t.Schedule(_obszar, Dzis, Stamp()));
        Dodaj("za trzy dni", t => t.Schedule(_obszar, Dzis.AddDays(3), Stamp()));
        Dodaj("za miesiąc", t => t.Schedule(_obszar, Dzis.AddDays(30), Stamp()));

        var wynik = await _zadania.UpcomingAsync(Dzis, Dzis.AddDays(14));

        wynik.Select(t => t.Title).Should().Equal("za trzy dni");
    }

    [Fact]
    public async Task Archiwum_zwraca_najnowsze_pierwsze_i_obejmuje_kosz()
    {
        var zegar = new Zegar();
        Dodaj("starsze", t => t.Complete(zegar.Now.AddDays(-2), Stamp()));
        Dodaj("nowsze", t => t.Complete(zegar.Now, Stamp()));
        Dodaj("wyrzucone", t => t.Trash(Stamp()));

        var wynik = await _zadania.ArchiveAsync(limit: 10);

        wynik.Should().HaveCount(3);
        wynik.First().Title.Should().Be("nowsze");
    }

    [Fact]
    public async Task Archiwum_szanuje_ograniczenie_liczby()
    {
        for (var i = 0; i < 5; i++)
        {
            Dodaj($"zadanie {i}", t => t.Complete(new Zegar().Now, Stamp()));
        }

        (await _zadania.ArchiveAsync(limit: 2)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Obszar_zwraca_tylko_swoje_zadania()
    {
        var inny = Guid.CreateVersion7();
        Dodaj("moje");
        Dodaj("cudze", t => t.MakeNext(inny, Stamp()));

        var wynik = await _zadania.ByAreaAsync(_obszar);

        wynik.Select(t => t.Title).Should().Equal("moje");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
