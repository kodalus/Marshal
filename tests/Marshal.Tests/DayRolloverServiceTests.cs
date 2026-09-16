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
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();
    private readonly DayRolloverService _usluga;
    private readonly Guid _obszar = Guid.CreateVersion7();

    public DayRolloverServiceTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();

        var repo = new TaskRepository(_db);
        _usluga = new DayRolloverService(
            repo, new UnitOfWork(_db), _zegar, new HlcSource(_zegar, "biurko"));
    }

    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    private TaskItem Dodaj(string tytul, string doDate, RecurrenceRule? regula = null)
    {
        var hlc = new HlcSource(_zegar, "biurko");
        var zadanie = TaskItem.Capture(tytul, _zegar.Now, hlc.Next());
        zadanie.Schedule(_obszar, D(doDate), hlc.Next());

        if (regula is not null)
        {
            zadanie.SetRecurrence(regula, hlc.Next());
        }

        _db.Tasks.Add(zadanie);
        _db.SaveChanges();
        return zadanie;
    }

    [Fact]
    public async Task Pusta_baza_nie_ma_czego_przesuwac()
    {
        (await _usluga.RunAsync()).Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Zalegle_zaplanowane_laduja_na_dzis()
    {
        var id = Dodaj("Zadzwonić", "2026-09-10").Id;

        var raport = await _usluga.RunAsync();

        raport.Moved.Should().Be(1);
        _db.Tasks.Single(t => t.Id == id).DoDate.Should().Be(D("2026-09-16"));
    }

    [Fact]
    public async Task Zadanie_ze_skrzynki_nie_jest_ruszane()
    {
        // Skrzynka nie ma dnia wykonania i nie powinna go dostać z przypadku.
        var hlc = new HlcSource(_zegar, "biurko");
        var wrzut = TaskItem.Capture("do przemyślenia", _zegar.Now, hlc.Next());
        _db.Tasks.Add(wrzut);
        _db.SaveChanges();

        (await _usluga.RunAsync()).Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Drugie_uruchomienie_tego_samego_dnia_nic_nie_zmienia()
    {
        Dodaj("Zadzwonić", "2026-09-10");
        Dodaj("Podlać", "2026-09-12", new RecurrenceRule(RecurrenceKind.Daily));

        await _usluga.RunAsync();
        var drugie = await _usluga.RunAsync();

        drugie.Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Accumulate_domyka_cala_zaleglosc_w_jednym_przebiegu()
    {
        // Tydzień bez otwierania aplikacji ma dać tydzień pozycji od razu, a nie po
        // jednej na uruchomienie.
        Dodaj("Trening", "2026-09-09",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Accumulate));

        var raport = await _usluga.RunAsync();

        raport.Spawned.Should().Be(7);

        // Jedno umówione na dziś i siedem zaległości bez dnia — a nie osiem pozycji
        // stłoczonych na dzisiaj, co wyszłoby z dosłownego czytania 8.7.
        _db.Tasks.Count(t => t.State == TaskState.Scheduled).Should().Be(1);
        _db.Tasks.Count(t => t.State == TaskState.Next).Should().Be(7);
        _db.Tasks.Single(t => t.RecurrenceJson != null).DoDate.Should().Be(D("2026-09-16"));

        (await _usluga.RunAsync()).Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Skip_po_tygodniu_zostawia_jedno_zywe_wystapienie()
    {
        Dodaj("Wynieść śmieci", "2026-09-09",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Skip));

        var raport = await _usluga.RunAsync();

        raport.Spawned.Should().Be(1);
        _db.Tasks.Count(t => t.State == TaskState.Scheduled).Should().Be(1);
        _db.Tasks.Count(t => t.State == TaskState.Trashed).Should().Be(1);
    }

    [Fact]
    public async Task Carry_zostawia_jedna_pozycje_z_data_pierwszego_przegapienia()
    {
        var id = Dodaj("Zapłacić", "2026-09-09", new RecurrenceRule(RecurrenceKind.Daily)).Id;

        await _usluga.RunAsync();

        var zadanie = _db.Tasks.Single(t => t.Id == id);
        zadanie.DoDate.Should().Be(D("2026-09-16"));
        zadanie.CarriedSince.Should().Be(D("2026-09-09"));
        _db.Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task Regula_przezywa_zapis_do_bazy_i_odczyt()
    {
        // Reguła idzie do bazy jako tekst. Gdyby odczyt jej nie odtwarzał, przejście
        // dnia widziałoby zwykłe zadanie i cicho zgubiłoby rytm.
        Dodaj("Podlać", "2026-09-15",
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
        _polaczenie.Dispose();
    }
}
