using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

public sealed class InboxServiceTests : IDisposable
{
    private sealed class ZegarStojacy : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection;
    private readonly MarshalDbContext _db;
    private readonly InboxService _skrzynka;
    private readonly ZegarStojacy _zegar = new();
    private readonly Guid _obszar = Guid.CreateVersion7();

    public InboxServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _skrzynka = new InboxService(
            new TaskRepository(_db),
            new ProjectRepository(_db),
            new UnitOfWork(_db),
            _zegar,
            new HlcSource(_zegar, "testy"));
    }

    private async Task<Guid> Wrzut(string tytul = "Zadzwonić do przychodni") =>
        await _skrzynka.CaptureAsync(tytul);

    private TaskItem Wczytaj(Guid id) => _db.Tasks.Single(t => t.Id == id);

    [Fact]
    public async Task Wrzut_laduje_w_skrzynce_i_liczy_sie_do_licznika()
    {
        await Wrzut("pierwsza myśl");
        await Wrzut("druga myśl");

        (await _skrzynka.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Skrzynka_zwraca_najstarsze_pierwsze()
    {
        await Wrzut("pierwsza");
        _zegar.Now = _zegar.Now.AddMinutes(5);
        await Wrzut("druga");

        var pozycje = await _skrzynka.ListAsync();

        pozycje.Select(p => p.Title).Should().Equal("pierwsza", "druga");
    }

    [Fact]
    public async Task Kosz_zabiera_ze_skrzynki_ale_zostawia_rekord()
    {
        var id = await Wrzut();

        await _skrzynka.TrashAsync(id);

        (await _skrzynka.CountAsync()).Should().Be(0);
        Wczytaj(id).State.Should().Be(TaskState.Trashed);
        Wczytaj(id).Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task Dwuminutowka_jest_od_razu_wykonana()
    {
        var id = await Wrzut();

        await _skrzynka.DoNowAsync(id, _obszar);

        var zadanie = Wczytaj(id);
        zadanie.State.Should().Be(TaskState.Done);
        zadanie.CompletedAt.Should().Be(_zegar.Now);
        zadanie.AreaId.Should().Be(_obszar);
    }

    [Fact]
    public async Task Oddelegowanie_liczy_oczekiwanie_od_dzisiaj()
    {
        var id = await Wrzut("Wniosek rozpatrzony przez urząd");

        await _skrzynka.DelegateAsync(id, _obszar, "urząd miasta", nudgeDays: 21);

        var zadanie = Wczytaj(id);
        zadanie.State.Should().Be(TaskState.Waiting);
        zadanie.WaitingForWho.Should().Be("urząd miasta");
        zadanie.WaitingSince.Should().Be(DateOnly.FromDateTime(_zegar.Now.Date));
        zadanie.WaitingNudgeDays.Should().Be(21);
    }

    [Fact]
    public async Task Zaplanowanie_zapisuje_dzien_i_opuszcza_skrzynke()
    {
        var id = await Wrzut();
        var dzien = new DateOnly(2026, 9, 21);

        await _skrzynka.ScheduleAsync(id, _obszar, dzien);

        Wczytaj(id).State.Should().Be(TaskState.Scheduled);
        Wczytaj(id).DoDate.Should().Be(dzien);
        (await _skrzynka.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Nastepna_akcja_moze_od_razu_trafic_do_projektu()
    {
        var pierwsza = await Wrzut("Zebrać dokumenty");
        var projekt = await _skrzynka.PromoteToProjectAsync(
            pierwsza, _obszar, "Wniosek jest złożony", "Zebrać dokumenty");

        var kolejna = await Wrzut("Umówić termin w urzędzie");
        await _skrzynka.MakeNextAsync(kolejna, _obszar, projekt);

        Wczytaj(kolejna).ProjectId.Should().Be(projekt);
    }

    [Fact]
    public async Task Projekt_rodzi_sie_z_pierwsza_akcja_wiec_nigdy_zablokowany()
    {
        var id = await Wrzut("Zrobić coś z wnioskiem");

        var projekt = await _skrzynka.PromoteToProjectAsync(
            id, _obszar, "Wniosek jest złożony", "Zebrać listę wymaganych dokumentów");

        var utworzony = _db.Projects.Single(p => p.Id == projekt);
        utworzony.Outcome.Should().Be("Wniosek jest złożony");
        utworzony.AreaId.Should().Be(_obszar);

        var akcja = Wczytaj(id);
        akcja.Title.Should().Be("Zebrać listę wymaganych dokumentów");
        akcja.State.Should().Be(TaskState.Next);
        akcja.ProjectId.Should().Be(projekt);
    }

    [Fact]
    public async Task Kiedys_moze_moze_dostac_date_powrotu()
    {
        var id = await Wrzut("Nauczyć się hiszpańskiego");
        var powrot = new DateOnly(2027, 1, 1);

        await _skrzynka.PostponeAsync(id, _obszar, powrot);

        Wczytaj(id).State.Should().Be(TaskState.Someday);
        Wczytaj(id).DeferUntil.Should().Be(powrot);
    }

    [Fact]
    public async Task Przetworzenie_nieistniejacej_pozycji_jest_bledem()
    {
        var przetworz = async () => await _skrzynka.TrashAsync(Guid.CreateVersion7());

        await przetworz.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Kazdy_zapis_dostaje_wiekszy_znacznik_zegara()
    {
        var id = await Wrzut();
        var poWrzucie = Wczytaj(id).UpdatedAt;

        await _skrzynka.MakeNextAsync(id, _obszar);

        Wczytaj(id).UpdatedAt.Should().BeGreaterThan(poWrzucie);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
