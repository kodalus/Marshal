using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Dwa zapytania o rytm starego modelu: jedno dla siatki, drugie dla przeniesienia.
/// </summary>
/// <remarks>
/// Rozdzielone celowo. Siatka pyta „co rozwinąć do przodu" i ma pomijać rytm odłożony
/// na kiedyś-może albo bez dnia — rozwinięcie nie miałoby od czego liczyć, a odłożony
/// nie ma się rysować. Przeniesienie pyta „co zostało w starym modelu" i musi wziąć
/// jedno i drugie, bo wiersz pominięty raz zostaje w starym modelu na stałe.
/// </remarks>
public sealed class LegacyRhythmQueryTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(1));
    }

    private static readonly DateOnly Today = new(2026, 11, 27);
    private static readonly RecurrenceRule Daily = new(RecurrenceKind.Daily);

    private readonly SqliteConnection _connection;
    private readonly MarshalDbContext _db;
    private readonly TaskRepository _tasks;
    private readonly Guid _area = Guid.CreateVersion7();
    private long _stamp = 1000;

    public LegacyRhythmQueryTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _tasks = new TaskRepository(_db);
    }

    private Hlc Stamp() => new(_stamp += 10, 0, "t");

    private TaskItem Add(string title, Action<TaskItem> set)
    {
        var task = TaskItem.Capture(title, new Clock().Now, Stamp());
        set(task);
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    private async Task<IEnumerable<string>> LegacyAsync() =>
        (await _tasks.LegacyRhythmsAsync()).Select(t => t.Title);

    private async Task<IEnumerable<string>> GridAsync() =>
        (await _tasks.RecurringAsync()).Select(t => t.Title);

    [Fact]
    public async Task Przeniesienie_bierze_rytm_zaplanowany()
    {
        Add("codziennie", t =>
        {
            t.Schedule(_area, Today, Stamp());
            t.SetRecurrence(Daily, Stamp());
        });

        (await LegacyAsync()).Should().ContainSingle().Which.Should().Be("codziennie");
    }

    [Fact]
    public async Task Przeniesienie_bierze_rytm_bez_dnia_wykonania()
    {
        Add("bez dnia", t =>
        {
            t.MakeNext(_area, Stamp());
            t.SetRecurrence(Daily, Stamp());
        });

        (await LegacyAsync()).Should().ContainSingle().Which.Should().Be("bez dnia");
        (await GridAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Przeniesienie_bierze_rytm_odlozony_na_kiedys()
    {
        Add("kiedyś", t =>
        {
            t.Postpone(_area, Today.AddDays(30), Stamp());
            t.SetRecurrence(Daily, Stamp());
        });

        (await LegacyAsync()).Should().ContainSingle().Which.Should().Be("kiedyś");
        (await GridAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Przeniesienie_bierze_rytm_ze_skrzynki()
    {
        Add("w skrzynce", t => t.SetRecurrence(Daily, Stamp()));

        (await LegacyAsync()).Should().ContainSingle().Which.Should().Be("w skrzynce");
        (await GridAsync()).Should().BeEmpty();
    }

    /// <remarks>
    /// Przy odhaczeniu reguła schodzi na następnik, więc odhaczony wiersz z regułą to
    /// ślad zapisu, który doszedł nie w swojej kolejności. Przeniesienie zrobiłoby
    /// z niego żywą serię — czyli wskrzesiłoby rytm już domknięty.
    /// </remarks>
    [Fact]
    public async Task Przeniesienie_pomija_rytm_odhaczony()
    {
        Add("odhaczone", t =>
        {
            t.Schedule(_area, Today, Stamp());
            t.SetRecurrence(Daily, Stamp());
            t.Complete(new Clock().Now, Stamp());
        });

        (await LegacyAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Przeniesienie_pomija_rytm_w_koszu()
    {
        Add("w koszu", t =>
        {
            t.Schedule(_area, Today, Stamp());
            t.SetRecurrence(Daily, Stamp());
            t.Trash(Stamp());
        });

        (await LegacyAsync()).Should().BeEmpty();
    }

    /// <remarks>
    /// Wystąpienie już należące do serii jest **po** przeniesieniu, a nie przed nim:
    /// drugie przeniesienie założyłoby serię drugi raz.
    /// </remarks>
    [Fact]
    public async Task Przeniesienie_pomija_wystapienie_nalezace_do_serii()
    {
        Add("już w serii", t =>
        {
            t.Schedule(_area, Today, Stamp());
            t.SetRecurrence(Daily, Stamp());
            t.JoinSeries(Guid.CreateVersion7(), Stamp());
        });

        (await LegacyAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Przeniesienie_pomija_zadanie_bez_reguly()
    {
        Add("bez reguły", t => t.Schedule(_area, Today, Stamp()));

        (await LegacyAsync()).Should().BeEmpty();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
