using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
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
/// Serie na prawdziwej bazie: dopełnianie okna, nagrobki i zmiana rytmu.
/// </summary>
/// <remarks>
/// Trzy obietnice, od których zależy cała przebudowa, i każda ma tu swój test.
/// Dopełnianie wolno puszczać ile razy się chce. Dzień skasowany nie wraca. Zmiana
/// serii nie dotyka dnia, który ktoś zmienił z ręki.
/// </remarks>
public sealed class SeriesServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly HlcSource _hlc;
    private readonly SeriesService _rhythms;
    private readonly Guid _area = Guid.CreateVersion7();

    public SeriesServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();

        _hlc = new HlcSource(_clock, "biurko");
        _rhythms = new SeriesService(
            new TaskSeriesRepository(_db), new TaskRepository(_db),
            new UnitOfWork(_db), _clock, _hlc);
    }

    private DateOnly Today => ((IClock)_clock).Today;

    private async Task<TaskSeries> Start(string title = "Wynieść śmieci", int? interval = null)
    {
        var task = TaskItem.Capture(title, _clock.Now, _hlc.Next());
        task.Schedule(_area, Today, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        var rule = interval is { } every
            ? new RecurrenceRule(RecurrenceKind.EveryNDays, every,
                anchor: RecurrenceAnchor.FromScheduled)
            : new RecurrenceRule(RecurrenceKind.Daily);

        return await _rhythms.StartAsync(task, rule);
    }

    private List<TaskItem> Days(TaskSeries one) =>
        [.. _db.Tasks.Where(t => t.SeriesId == one.Id).OrderBy(t => t.DoDate)];

    [Fact]
    public async Task Zalozenie_serii_zostawia_zadanie_i_stawia_okno()
    {
        var one = await Start();

        // Zadanie zostaje tym, czym było — razem z załącznikami i wszystkim, co ktoś
        // do niego dopisał, zanim nadał mu rytm.
        var first = Days(one)[0];

        first.DoDate.Should().Be(Today);
        first.Overridden.Should().BeTrue("pierwsze wystąpienie było zadaniem");
        first.Recurrence.Should().BeNull("regułę nosi teraz seria");

        Days(one).Count.Should().BeGreaterThan(SeriesWindow.Ahead);
    }

    [Fact]
    public async Task Dopelnianie_wolno_puszczac_ile_razy_sie_chce()
    {
        // Warunek, nie wygoda: dwa urządzenia dopełniają okno niezależnie i bez
        // umawiania się, które ma.
        var one = await Start();
        var after = Days(one).Count;

        (await _rhythms.TopUpAsync(one)).Added.Should().Be(0);
        (await _rhythms.TopUpAsync()).Added.Should().Be(0);

        Days(one).Count.Should().Be(after);
    }

    [Fact]
    public async Task Skasowany_dzien_nie_wraca_przy_dopelnieniu()
    {
        // Sedno różnicy wobec starego modelu: „tej środy nie będzie" jest zapisem
        // trwałym, a nie odwołaniem doklejonym do reguły, które przy pierwszym
        // obcięciu minionych zmian przepadało.
        var one = await Start();
        var day = Today.AddDays(3);
        var victim = Days(one).Single(z => z.DoDate == day);

        victim.Trash(_hlc.Next());
        _db.SaveChanges();

        (await _rhythms.TopUpAsync(one)).Added.Should().Be(0);

        Days(one).Where(z => z.DoDate == day)
            .Should().ContainSingle().Which.State.Should().Be(TaskState.Trashed);
    }

    [Fact]
    public async Task Koniec_serii_na_dniu_zabiera_to_co_stalo_dalej()
    {
        // Sama data końca zatrzymałaby dokładanie, ale zostawiłaby na siatce wystąpienia
        // postawione wcześniej — czyli odpowiedziałaby „już nie będzie" na ekranie,
        // na którym widać, że będzie.
        var one = await Start();
        var last = Today.AddDays(2);

        await _rhythms.EndAsync(one, last);

        Days(one).Where(z => z.DoDate > last)
            .Should().OnlyContain(z => z.State == TaskState.Trashed);

        Days(one).Single(z => z.DoDate == last).State.Should().Be(TaskState.Scheduled);

        (await _rhythms.TopUpAsync(one)).Added.Should().Be(0, "nie ma już czego dokładać");
    }

    [Fact]
    public async Task Zmiana_rytmu_stawia_okno_od_nowa_a_zmienionego_dnia_nie_rusza()
    {
        // Ta jedna decyzja, której model z zapisanym oknem nie da się ominąć, i jest
        // rozstrzygnięta na korzyść dnia: ten jeden wtorek, który ktoś świadomie
        // przestawił, był ostatnią świadomą decyzją o tym dniu.
        var one = await Start();
        var pinned = Days(one).Single(z => z.DoDate == Today.AddDays(4));

        pinned.Override(_hlc.Next());
        _db.SaveChanges();

        var report = await _rhythms.ChangeAsync(
            one,
            rule: new RecurrenceRule(
                RecurrenceKind.EveryNDays, 3, anchor: RecurrenceAnchor.FromScheduled));

        report.Retired.Should().BeGreaterThan(0);
        report.Added.Should().BeGreaterThan(0);

        _db.Tasks.Single(t => t.Id == pinned.Id).State
            .Should().Be(TaskState.Scheduled, "zmienione z ręki zostaje");

        // Nowe wystąpienia idą co trzy dni od początku serii.
        Days(one)
            .Where(z => z.State == TaskState.Scheduled && !z.Overridden)
            .Should().OnlyContain(z => (z.DoDate!.Value.DayNumber - Today.DayNumber) % 3 == 0);
    }

    [Fact]
    public async Task Skasowanie_serii_zostawia_historie_a_zabiera_przyszlosc()
    {
        var one = await Start();

        // Wczorajsze wystąpienie, jakby seria chodziła od wczoraj.
        var past = TaskItem.InSeries(
            one.Id,
            new SeriesSlot(OccurrenceId.For(one.Id, Today.AddDays(-1)), Today.AddDays(-1), null, null),
            one.Template,
            _clock.Now,
            _hlc.Next());

        past.Complete(_clock.Now, _hlc.Next());
        _db.Tasks.Add(past);
        _db.SaveChanges();

        await _rhythms.DropAsync(one);

        _db.Tasks.Single(t => t.Id == past.Id).State
            .Should().Be(TaskState.Done, "zdarzyło się i historia o tym wie");

        Days(one).Where(z => z.DoDate >= Today)
            .Should().OnlyContain(z => z.State == TaskState.Trashed);

        _db.TaskSeries.Single(z => z.Id == one.Id).Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task Dwa_urzadzenia_dopelniajace_okno_osobno_daja_te_same_wiersze()
    {
        // To jest cała przebudowa w jednym zdaniu. Stary model liczył tożsamość
        // następnika z poprzednika i z dnia, w którym urządzenie akurat było otwarte,
        // więc telefon i pulpit rozchodziły się na dwa rozłączne łańcuchy.
        var one = await Start();

        // Dni, nie identyfikatory: pierwszy dzień obsadza zadanie, które istniało przed
        // serią, i niesie swój dawny identyfikator. Okno to wie — pomija dzień już
        // zajęty — więc zgodność jest o dniach, nie o tożsamościach.
        var mine = Days(one).Where(z => z.DoDate is not null)
            .Select(z => z.DoDate!.Value).ToHashSet();

        // Drugie urządzenie: ta sama seria, własny zegar logiczny, okno liczone u siebie.
        var theirs = SeriesWindow.Plan(one, Today).Select(z => z.Date).ToHashSet();

        theirs.Should().BeEquivalentTo(mine);

        // A tam, gdzie wiersz powstał z okna, tożsamość jest wyliczona — i to jest
        // ta rzecz, która zamyka podwajanie się serii między urządzeniami.
        foreach (var day in Days(one).Where(z => z.DoDate > Today))
        {
            day.Id.Should().Be(OccurrenceId.For(one.Id, day.DoDate!.Value));
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
