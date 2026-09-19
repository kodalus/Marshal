using FluentAssertions;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Marshal.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Sprawdza odwzorowanie na bazę na prawdziwym SQLite w pamięci — nie na dostawcy
/// InMemory, który milczy o błędach schematu i typów.
/// </summary>
public sealed class MarshalDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public MarshalDbContextTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
    }

    private MarshalDbContext Kontekst()
    {
        var options = new DbContextOptionsBuilder<MarshalDbContext>()
            .UseSqlite(_connection)
            .Options;

        var kontekst = new MarshalDbContext(options);
        kontekst.Database.Migrate();
        return kontekst;
    }

    [Fact]
    public void Model_daje_sie_zbudowac_i_utworzyc_schemat()
    {
        using var kontekst = Kontekst();

        kontekst.Model.Should().NotBeNull();
    }

    [Fact]
    public void Obszar_przechodzi_zapis_i_odczyt_bez_utraty_pol()
    {
        var id = Guid.CreateVersion7();
        var utworzony = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
        var znacznik = new Hlc(1757942400123, 7, "a3f1");

        using (var zapis = Kontekst())
        {
            zapis.Areas.Add(new Area(id, utworzony, znacznik, "Sprawy urzędowe", 9.0, 60, 21, "#8899AA"));
            zapis.SaveChanges();
        }

        using var odczyt = Kontekst();
        var area = odczyt.Areas.Single();

        area.Id.Should().Be(id);
        area.Name.Should().Be("Sprawy urzędowe");
        area.Color.Should().Be("#8899AA");
        area.SortOrder.Should().Be(9.0);
        area.QuietDays.Should().Be(60);
        area.DefaultNudgeDays.Should().Be(21);
        area.UpdatedAt.Should().Be(znacznik);
        area.CreatedAt.Should().Be(utworzony);
    }

    [Fact]
    public void Znacznik_zegara_jest_skladowany_jako_tekst()
    {
        using var kontekst = Kontekst();
        kontekst.Areas.Add(new Area(
            Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, new Hlc(1757942400123, 7, "a3f1"), "Dom", 4.0));
        kontekst.SaveChanges();

        using var polecenie = _connection.CreateCommand();
        polecenie.CommandText = "SELECT UpdatedAt FROM Areas LIMIT 1";

        polecenie.ExecuteScalar().Should().Be("1757942400123.000007.a3f1");
    }

    [Fact]
    public void Identyfikator_jest_skladowany_jako_tekst()
    {
        var id = Guid.CreateVersion7();

        using var kontekst = Kontekst();
        kontekst.Areas.Add(new Area(id, DateTimeOffset.UnixEpoch, new Hlc(1, 0, "a"), "Praca", 1.0));
        kontekst.SaveChanges();

        using var polecenie = _connection.CreateCommand();
        polecenie.CommandText = "SELECT Id FROM Areas LIMIT 1";

        polecenie.ExecuteScalar().Should().BeOfType<string>();
    }

    [Fact]
    public void Nagrobek_zostaje_w_tabeli()
    {
        var id = Guid.CreateVersion7();

        using var kontekst = Kontekst();
        var area = new Area(id, DateTimeOffset.UnixEpoch, new Hlc(1, 0, "a"), "Relacje", 10.0);
        kontekst.Areas.Add(area);
        kontekst.SaveChanges();

        area.MarkDeleted(new Hlc(2, 0, "a"));
        kontekst.SaveChanges();

        kontekst.Areas.Single().Deleted.Should().BeTrue();
    }

    public void Dispose() => _connection.Dispose();
}
