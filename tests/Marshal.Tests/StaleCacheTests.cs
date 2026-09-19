using FluentAssertions;
using Marshal.Domain.Filters;
using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Marshal.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Odczyt zapamiętany w encji kontra wartość wpisana z boku przez scalanie (spec 9.4).
/// </summary>
/// <remarks>
/// Scalanie i wgranie kopii wpisują wartość wprost do właściwości, omijając metodę
/// domenową. Przy pojedynczym kontekście bazy encja zostaje śledzona przez całe życie
/// aplikacji, więc raz rozłożona reguła wisiałaby w pamięci do ponownego uruchomienia.
/// </remarks>
public sealed class StaleCacheTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 17, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();
    private readonly HlcSource _hlc;
    private readonly Guid _obszar = Guid.CreateVersion7();

    public StaleCacheTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_zegar, "biurko");
    }

    /// <summary>Wpisanie wartości tak, jak robi to scalanie: prosto we właściwość.</summary>
    private void WpiszZBoku<T>(T encja, string pole, object? wartosc)
        where T : class =>
        _db.Entry(encja).Property(pole).CurrentValue = wartosc;

    [Fact]
    public void Regula_powtarzania_odswieza_sie_po_wpisaniu_z_boku()
    {
        var task = TaskItem.Capture("podlać kwiaty", _zegar.Now, _hlc.Next());
        task.MakeNext(_obszar, _hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday), _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();

        // Odczyt, który zapełnia pamięć podręczną.
        task.Recurrence!.Kind.Should().Be(RecurrenceKind.Weekly);

        // Drugie urządzenie zmieniło rytm na codzienny.
        WpiszZBoku(
            task,
            nameof(TaskItem.RecurrenceJson),
            new RecurrenceRule(RecurrenceKind.Daily).ToJson());

        // Bez wiązania pamięci podręcznej z tekstem przejście dnia rodziłoby
        // wystąpienia w rytmie, który już nie obowiązuje.
        task.Recurrence!.Kind.Should().Be(RecurrenceKind.Daily);
    }

    [Fact]
    public void Zapisany_widok_odswieza_sie_po_wpisaniu_z_boku()
    {
        var filter = SavedFilter.Create(
            "Kwadrans",
            new FilterQuery([FilterCondition.Estimate(15)]),
            _zegar.Now,
            _hlc.Next());

        _db.SavedFilters.Add(filter);
        _db.SaveChanges();

        filter.Query!.Conditions[0].MaxMinutes.Should().Be(15);

        WpiszZBoku(
            filter,
            nameof(SavedFilter.DefinitionJson),
            new FilterQuery([FilterCondition.Estimate(60)]).ToJson());

        filter.Query!.Conditions[0].MaxMinutes.Should().Be(60);
    }

    [Fact]
    public void Wyczyszczenie_reguly_z_boku_tez_widac()
    {
        var task = TaskItem.Capture("podlać kwiaty", _zegar.Now, _hlc.Next());
        task.MakeNext(_obszar, _hlc.Next());
        task.SetRecurrence(new RecurrenceRule(RecurrenceKind.Daily), _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();

        task.Recurrence.Should().NotBeNull();

        WpiszZBoku(task, nameof(TaskItem.RecurrenceJson), null);

        task.Recurrence.Should().BeNull();
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}
