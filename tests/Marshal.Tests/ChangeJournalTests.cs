using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

public sealed class ChangeJournalTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection;
    private readonly MarshalDbContext _db;
    private readonly Guid _obszar = Guid.CreateVersion7();
    private long _znacznik = 1000;

    public ChangeJournalTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.EnsureCreated();
    }

    private Hlc Stamp() => new(_znacznik += 10, 0, "biurko");

    private TaskItem Zapisz(string tytul = "Zadzwonić do przychodni")
    {
        var zadanie = TaskItem.Capture(tytul, new Zegar().Now, Stamp());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();
        return zadanie;
    }

    [Fact]
    public void Nowa_encja_zapisuje_wpis_dla_kazdego_wypelnionego_pola()
    {
        Zapisz();

        var wpisy = _db.Changes.ToList();
        wpisy.Should().NotBeEmpty();
        wpisy.Should().OnlyContain(w => w.EntityType == "Tasks");
        wpisy.Select(w => w.Field).Should().Contain(["Title", "State", "CreatedAt", "UpdatedAt"]);
    }

    [Fact]
    public void Klucz_glowny_nie_trafia_do_dziennika_jako_pole()
    {
        Zapisz();

        _db.Changes.Select(w => w.Field).Should().NotContain("Id");
    }

    [Fact]
    public void Puste_pola_nowej_encji_nie_zasmiecaja_dziennika()
    {
        Zapisz();

        _db.Changes.Select(w => w.Field).Should().NotContain(["Deadline", "WaitingForWho", "ProjectId"]);
    }

    [Fact]
    public void Zmiana_zapisuje_wylacznie_pola_faktycznie_zmienione()
    {
        var zadanie = Zapisz();
        var poDodaniu = _db.Changes.Count();

        zadanie.Rename("Zadzwonić do przychodni po skierowanie", Stamp());
        _db.SaveChanges();

        var nowe = _db.Changes.Skip(poDodaniu).ToList();
        nowe.Select(w => w.Field).Should().BeEquivalentTo("Title", "UpdatedAt");
    }

    [Fact]
    public void Wpis_niesie_wartosc_w_postaci_bazodanowej()
    {
        Zapisz("kupić mleko");

        _db.Changes.Single(w => w.Field == "Title").Value.Should().Be("\"kupić mleko\"");

        // Stan jest enumem konwertowanym na liczbę — dziennik niesie liczbę,
        // nie nazwę, więc nie zależy od nazw w kodzie.
        _db.Changes.Single(w => w.Field == "State").Value.Should().Be("0");
    }

    [Fact]
    public void Polskie_znaki_nie_sa_uciekane()
    {
        // Format tekstowy ma sens tylko wtedy, gdy da się go czytać. Domyślny
        // serializator zamieniłby każdą polską literę na sekwencję \uXXXX.
        Zapisz("zażółć gęślą jaźń");

        var wartosc = _db.Changes.Single(w => w.Field == "Title").Value;

        wartosc.Should().NotContain("\\u");
        wartosc.Should().Contain("zażółć gęślą jaźń");
    }

    [Fact]
    public void Znacznik_wpisu_jest_znacznikiem_encji()
    {
        var zadanie = Zapisz();

        _db.Changes.Should().OnlyContain(w => w.Hlc == zadanie.UpdatedAt.ToString());
    }

    [Fact]
    public void Znaczniki_pol_powstaja_i_sa_podnoszone_przy_zmianie()
    {
        var zadanie = Zapisz();
        var tytulPrzed = _db.FieldStamps.Single(f => f.Field == "Title").Hlc;

        zadanie.Rename("inny tytuł", Stamp());
        _db.SaveChanges();

        var tytulPo = _db.FieldStamps.Single(f => f.Field == "Title").Hlc;
        tytulPo.Should().NotBe(tytulPrzed);
        Hlc.Parse(tytulPo).Should().BeGreaterThan(Hlc.Parse(tytulPrzed));
    }

    [Fact]
    public void Pole_nietkniete_zachowuje_swoj_wczesniejszy_znacznik()
    {
        // To jest cały sens tabeli: bez niej po zmianie tytułu nie dałoby się
        // stwierdzić, że lokalna waga pochodzi sprzed tej zmiany.
        var zadanie = Zapisz();
        var stanPrzed = _db.FieldStamps.Single(f => f.Field == "State").Hlc;

        zadanie.Rename("inny tytuł", Stamp());
        _db.SaveChanges();

        _db.FieldStamps.Single(f => f.Field == "State").Hlc.Should().Be(stanPrzed);
    }

    [Fact]
    public void Kazde_pole_ma_dokladnie_jeden_znacznik()
    {
        var zadanie = Zapisz();
        zadanie.Rename("raz", Stamp());
        _db.SaveChanges();
        zadanie.Rename("dwa", Stamp());
        _db.SaveChanges();

        _db.FieldStamps.Count(f => f.Field == "Title").Should().Be(1);
    }

    [Fact]
    public void Dziennik_nie_zapisuje_samego_siebie()
    {
        Zapisz();

        _db.Changes.Select(w => w.EntityType).Should().NotContain(["Changes", "FieldStamps"]);
    }

    [Fact]
    public void Wpisy_zaczynaja_jako_niewyslane()
    {
        Zapisz();

        _db.Changes.Should().OnlyContain(w => !w.Sent);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
