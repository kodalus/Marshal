using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
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
/// Przejście dnia na prawdziwej bazie: to tu widać, czy zapytanie wybiera właściwe
/// zadania i czy powtórne wołanie naprawdę nic nie dokłada.
/// </summary>
public sealed class DayRolloverServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly DayRolloverService _service;
    private readonly HlcSource _hlc;
    private readonly Guid _area = Guid.CreateVersion7();

    public DayRolloverServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();

        // Jeden zegar logiczny na urządzenie, tak jak w aplikacji. Dwa niezależne
        // ruszające od zera wydałyby te same znaczniki dwa razy i drugi zapis zostałby
        // odrzucony jako cofnięcie zegara — co jest zachowaniem prawidłowym.
        _hlc = new HlcSource(_clock, "biurko");
        _service = new DayRolloverService(
            new TaskRepository(_db), new UnitOfWork(_db), _clock, _hlc);
    }

    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    private TaskItem Add(string title, string doDate, RecurrenceRule? rule = null)
    {
        var task = TaskItem.Capture(title, _clock.Now, _hlc.Next());
        task.Schedule(_area, D(doDate), _hlc.Next());

        if (rule is not null)
        {
            task.SetRecurrence(rule, _hlc.Next());
        }

        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    [Fact]
    public async Task Pusta_baza_nie_ma_czego_przesuwac()
    {
        (await _service.RunAsync()).Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Zalegle_zaplanowane_laduja_na_dzis()
    {
        var id = Add("Zadzwonić", "2026-09-10").Id;

        var report = await _service.RunAsync();

        report.Moved.Should().Be(1);
        _db.Tasks.Single(t => t.Id == id).DoDate.Should().Be(D("2026-09-16"));
    }

    [Fact]
    public async Task Zadanie_ze_skrzynki_nie_jest_ruszane()
    {
        // Skrzynka nie ma dnia wykonania i nie powinna go dostać z przypadku.
        var capture = TaskItem.Capture("do przemyślenia", _clock.Now, _hlc.Next());
        _db.Tasks.Add(capture);
        _db.SaveChanges();

        (await _service.RunAsync()).Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Drugie_uruchomienie_tego_samego_dnia_nic_nie_zmienia()
    {
        Add("Zadzwonić", "2026-09-10");
        Add("Podlać", "2026-09-12", new RecurrenceRule(RecurrenceKind.Daily));

        await _service.RunAsync();
        var drugie = await _service.RunAsync();

        drugie.Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Accumulate_domyka_cala_zaleglosc_w_jednym_przebiegu()
    {
        // Tydzień bez otwierania aplikacji ma dać tydzień pozycji od razu, a nie po
        // jednej na uruchomienie.
        Add("Trening", "2026-09-09",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Accumulate));

        var report = await _service.RunAsync();

        report.Spawned.Should().Be(7);

        // Jedno umówione na dziś i siedem zaległości bez dnia — a nie osiem pozycji
        // stłoczonych na dzisiaj, co wyszłoby z dosłownego czytania 8.7.
        _db.Tasks.Count(t => t.State == TaskState.Scheduled).Should().Be(1);
        _db.Tasks.Count(t => t.State == TaskState.Next).Should().Be(7);
        _db.Tasks.Single(t => t.RecurrenceJson != null).DoDate.Should().Be(D("2026-09-16"));

        (await _service.RunAsync()).Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Skip_po_tygodniu_zostawia_jedno_zywe_wystapienie()
    {
        Add("Wynieść śmieci", "2026-09-09",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Skip));

        var report = await _service.RunAsync();

        report.Spawned.Should().Be(1);
        _db.Tasks.Count(t => t.State == TaskState.Scheduled).Should().Be(1);
        _db.Tasks.Count(t => t.State == TaskState.Trashed).Should().Be(1);
    }

    [Fact]
    public async Task Carry_zostawia_jedna_pozycje_z_data_pierwszego_przegapienia()
    {
        var id = Add("Zapłacić", "2026-09-09", new RecurrenceRule(RecurrenceKind.Daily)).Id;

        await _service.RunAsync();

        var task = _db.Tasks.Single(t => t.Id == id);
        task.DoDate.Should().Be(D("2026-09-16"));
        task.CarriedSince.Should().Be(D("2026-09-09"));
        _db.Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task Regula_przezywa_zapis_do_bazy_i_odczyt()
    {
        // Reguła idzie do bazy jako tekst. Gdyby odczyt jej nie odtwarzał, przejście
        // dnia widziałoby zwykłe zadanie i cicho zgubiłoby rytm.
        Add("Podlać", "2026-09-15",
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Tuesday));

        _db.ChangeTracker.Clear();

        var odczytane = _db.Tasks.Single();
        odczytane.Recurrence.Should().NotBeNull();
        odczytane.Recurrence!.Kind.Should().Be(RecurrenceKind.Weekly);
        odczytane.Recurrence.DaysOfWeek.Should().Be(Weekdays.Tuesday);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
