using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

public sealed class AreaSeedTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now => new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection;
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();

    public AreaSeedTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    private Task Zaloz() => AreaSeed.EnsureAsync(_db, _zegar, new HlcSource(_zegar, "testy"));

    [Fact]
    public async Task Zaklada_dziesiec_obszarow()
    {
        await Zaloz();

        _db.Areas.Should().HaveCount(10);
    }

    [Fact]
    public async Task Progi_odpowiadaja_tabeli_ze_specyfikacji()
    {
        await Zaloz();

        var urzedowe = _db.Areas.Single(a => a.Name == "Sprawy urzędowe");
        urzedowe.QuietDays.Should().Be(60);
        urzedowe.DefaultNudgeDays.Should().Be(21);

        var zwiazek = _db.Areas.Single(a => a.Name == "Związek");
        zwiazek.DefaultNudgeDays.Should().Be(3);

        _db.Areas.Single(a => a.Name == "Zdrowie").QuietDays.Should().Be(30);
    }

    [Fact]
    public async Task Powtorne_zalozenie_nie_duplikuje()
    {
        await Zaloz();
        await Zaloz();

        _db.Areas.Should().HaveCount(10);
    }

    [Fact]
    public async Task Obszary_maja_rosnaca_kolejnosc()
    {
        await Zaloz();

        _db.Areas.OrderBy(a => a.SortOrder).Select(a => a.Name).First().Should().Be("Praca");
        _db.Areas.Select(a => a.SortOrder).Distinct().Should().HaveCount(10);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
