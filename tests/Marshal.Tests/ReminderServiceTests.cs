using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Notifications;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

public sealed class ReminderServiceTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();
    private readonly InAppNotifier _powiadamiacz = new();
    private readonly ReminderService _usluga;
    private readonly HlcSource _hlc;
    private readonly Guid _obszar = Guid.CreateVersion7();

    public ReminderServiceTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_zegar, "biurko");

        _usluga = new ReminderService(
            new TaskRepository(_db),
            new ReminderLog(_db),
            _powiadamiacz,
            new UnitOfWork(_db),
            _zegar);
    }

    private static DateTimeOffset Chwila(string iso) =>
        DateTimeOffset.Parse(iso + "+02:00");

    private TaskItem Dodaj(string tytul, string? przypomnienie)
    {
        var zadanie = TaskItem.Capture(tytul, _zegar.Now, _hlc.Next());
        zadanie.MakeNext(_obszar, _hlc.Next());

        if (przypomnienie is not null)
        {
            zadanie.SetReminder(Chwila(przypomnienie), _hlc.Next());
        }

        _db.Tasks.Add(zadanie);
        _db.SaveChanges();
        return zadanie;
    }

    [Fact]
    public async Task Przypomnienie_z_przyszlosci_milczy()
    {
        Dodaj("Zadzwonić", "2026-09-16T18:00:00");

        (await _usluga.RunAsync()).Should().Be(0);
        _powiadamiacz.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task Przypomnienie_wymagalne_odzywa_sie()
    {
        var id = Dodaj("Zadzwonić do przychodni", "2026-09-16T08:00:00").Id;

        (await _usluga.RunAsync()).Should().Be(1);

        var pokazane = _powiadamiacz.Drain();
        pokazane.Should().ContainSingle();
        pokazane[0].TaskId.Should().Be(id);
        pokazane[0].Title.Should().Be("Zadzwonić do przychodni");
    }

    [Fact]
    public async Task Przypomnienie_z_wczoraj_tez_sie_odzywa()
    {
        // Aplikacja nie chodzi w tle, więc chwila przypomnienia prawie nigdy nie zastaje
        // jej otwartej. Odzywanie się wyłącznie co do minuty znaczyłoby, że przypomnienia
        // nie działają w ogóle.
        Dodaj("Zapłacić ratę", "2026-09-14T20:00:00");

        (await _usluga.RunAsync()).Should().Be(1);
    }

    [Fact]
    public async Task To_samo_przypomnienie_nie_odzywa_sie_dwa_razy()
    {
        Dodaj("Zadzwonić", "2026-09-16T08:00:00");

        await _usluga.RunAsync();
        _powiadamiacz.Drain();

        (await _usluga.RunAsync()).Should().Be(0);
        _powiadamiacz.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task Przesuniete_przypomnienie_odzywa_sie_ponownie()
    {
        // „Przypomnij mi jednak o godzinę później" musi zadziałać. Gdyby zapis
        // pokazania znaczył tylko „o tym zadaniu już było", nowa chwila by przepadła.
        var zadanie = Dodaj("Zadzwonić", "2026-09-16T08:00:00");
        await _usluga.RunAsync();
        _powiadamiacz.Drain();

        zadanie.SetReminder(Chwila("2026-09-16T08:30:00"), _hlc.Next());
        _db.SaveChanges();

        (await _usluga.RunAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Wykonane_zadanie_nie_przypomina_o_sobie()
    {
        var zadanie = Dodaj("Zadzwonić", "2026-09-16T08:00:00");
        zadanie.Complete(_zegar.Now, _hlc.Next());
        _db.SaveChanges();

        (await _usluga.RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Zadanie_bez_przypomnienia_milczy()
    {
        Dodaj("Bez przypomnienia", null);

        (await _usluga.RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Zalegle_przypomnienia_ida_w_kolejnosci_chwil()
    {
        Dodaj("Trzecie", "2026-09-16T08:00:00");
        Dodaj("Pierwsze", "2026-09-14T08:00:00");
        Dodaj("Drugie", "2026-09-15T08:00:00");

        await _usluga.RunAsync();

        _powiadamiacz.Drain().Select(p => p.Title)
            .Should().Equal("Pierwsze", "Drugie", "Trzecie");
    }

    [Fact]
    public async Task Zapis_pokazania_nie_trafia_do_dziennika_zmian()
    {
        // Gdyby trafiał, drugie urządzenie dowiedziałoby się, że tu już pokazano —
        // i zamilkło, choć telefon leżał w torbie.
        Dodaj("Zadzwonić", "2026-09-16T08:00:00");
        var przed = _db.Changes.Count();

        await _usluga.RunAsync();

        _db.Changes.Count().Should().Be(przed);
        _db.ReminderShown.Should().ContainSingle();
    }

    [Fact]
    public async Task Przypomnienie_przenosi_sie_na_kolejne_wystapienie_z_zachowaniem_pory()
    {
        // „W przeddzień o dwudziestej" ma zostać przeddniem o dwudziestej, a nie
        // przenieść się co do daty i odezwać się natychmiast.
        var zadanie = TaskItem.Capture("Wynieść śmieci", _zegar.Now, _hlc.Next());
        zadanie.Schedule(_obszar, new DateOnly(2026, 9, 14), _hlc.Next());
        zadanie.SetReminder(Chwila("2026-09-13T20:00:00"), _hlc.Next());
        zadanie.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday), _hlc.Next());

        var nastepne = RecurrenceRunner.Complete(zadanie, _zegar.Now, _hlc.Next)!;

        nastepne.DoDate.Should().Be(new DateOnly(2026, 9, 21));
        nastepne.ReminderAt.Should().Be(Chwila("2026-09-20T20:00:00"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}
