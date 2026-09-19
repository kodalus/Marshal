using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
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
    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection;
    private readonly MarshalDbContext _db;
    private readonly InboxService _skrzynka;
    private readonly StoppedClock _clock = new();
    private readonly Guid _area = Guid.CreateVersion7();

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
            _clock,
            new HlcSource(_clock, "testy"),
            new NoTaskMirror());
    }

    private async Task<Guid> NewCapture(string title = "Zadzwonić do przychodni") =>
        await _skrzynka.CaptureAsync(title);

    private TaskItem Load(Guid id) => _db.Tasks.Single(t => t.Id == id);

    [Fact]
    public async Task Wrzut_laduje_w_skrzynce_i_liczy_sie_do_licznika()
    {
        await NewCapture("pierwsza myśl");
        await NewCapture("druga myśl");

        (await _skrzynka.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Skrzynka_zwraca_najstarsze_pierwsze()
    {
        await NewCapture("pierwsza");
        _clock.Now = _clock.Now.AddMinutes(5);
        await NewCapture("druga");

        var rows = await _skrzynka.ListAsync();

        rows.Select(p => p.Title).Should().Equal("pierwsza", "druga");
    }

    [Fact]
    public async Task Kosz_zabiera_ze_skrzynki_ale_zostawia_rekord()
    {
        var id = await NewCapture();

        await _skrzynka.TrashAsync(id);

        (await _skrzynka.CountAsync()).Should().Be(0);
        Load(id).State.Should().Be(TaskState.Trashed);
        Load(id).Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task Dwuminutowka_jest_od_razu_wykonana()
    {
        var id = await NewCapture();

        await _skrzynka.DoNowAsync(id, _area);

        var task = Load(id);
        task.State.Should().Be(TaskState.Done);
        task.CompletedAt.Should().Be(_clock.Now);
        task.AreaId.Should().Be(_area);
    }

    [Fact]
    public async Task Oddelegowanie_liczy_oczekiwanie_od_dzisiaj()
    {
        var id = await NewCapture("Wniosek rozpatrzony przez urząd");

        await _skrzynka.DelegateAsync(id, _area, "urząd miasta", nudgeDays: 21);

        var task = Load(id);
        task.State.Should().Be(TaskState.Waiting);
        task.WaitingForWho.Should().Be("urząd miasta");
        task.WaitingSince.Should().Be(DateOnly.FromDateTime(_clock.Now.Date));
        task.WaitingNudgeDays.Should().Be(21);
    }

    [Fact]
    public async Task Zaplanowanie_zapisuje_dzien_i_opuszcza_skrzynke()
    {
        var id = await NewCapture();
        var day = new DateOnly(2026, 9, 21);

        await _skrzynka.ScheduleAsync(id, _area, day);

        Load(id).State.Should().Be(TaskState.Scheduled);
        Load(id).DoDate.Should().Be(day);
        (await _skrzynka.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Nastepna_akcja_moze_od_razu_trafic_do_projektu()
    {
        var pierwsza = await NewCapture("Zebrać dokumenty");
        var project = await _skrzynka.PromoteToProjectAsync(
            pierwsza, _area, "Wniosek jest złożony", "Zebrać dokumenty");

        var kolejna = await NewCapture("Umówić termin w urzędzie");
        await _skrzynka.MakeNextAsync(kolejna, _area, project);

        Load(kolejna).ProjectId.Should().Be(project);
    }

    [Fact]
    public async Task Projekt_rodzi_sie_z_pierwsza_akcja_wiec_nigdy_zablokowany()
    {
        var id = await NewCapture("Zrobić coś z wnioskiem");

        var project = await _skrzynka.PromoteToProjectAsync(
            id, _area, "Wniosek jest złożony", "Zebrać listę wymaganych dokumentów");

        var utworzony = _db.Projects.Single(p => p.Id == project);
        utworzony.Outcome.Should().Be("Wniosek jest złożony");
        utworzony.AreaId.Should().Be(_area);

        var action = Load(id);
        action.Title.Should().Be("Zebrać listę wymaganych dokumentów");
        action.State.Should().Be(TaskState.Next);
        action.ProjectId.Should().Be(project);
    }

    [Fact]
    public async Task Kiedys_moze_moze_dostac_date_powrotu()
    {
        var id = await NewCapture("Nauczyć się hiszpańskiego");
        var back = new DateOnly(2027, 1, 1);

        await _skrzynka.PostponeAsync(id, _area, back);

        Load(id).State.Should().Be(TaskState.Someday);
        Load(id).DeferUntil.Should().Be(back);
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
        var id = await NewCapture();
        var poWrzucie = Load(id).UpdatedAt;

        await _skrzynka.MakeNextAsync(id, _area);

        Load(id).UpdatedAt.Should().BeGreaterThan(poWrzucie);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
