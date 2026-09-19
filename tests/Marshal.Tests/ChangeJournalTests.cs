using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Sync;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

public sealed class ChangeJournalTests : IDisposable
{
    private sealed class Clock : IClock
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
        _db.Database.Migrate();
    }

    private Hlc Stamp() => new(_znacznik += 10, 0, "biurko");

    private TaskItem Save(string title = "Zadzwonić do przychodni")
    {
        var task = TaskItem.Capture(title, new Clock().Now, Stamp());
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    [Fact]
    public void Nowa_encja_zapisuje_wpis_dla_kazdego_wypelnionego_pola()
    {
        Save();

        var entries = _db.Changes.ToList();
        entries.Should().NotBeEmpty();
        entries.Should().OnlyContain(w => w.EntityType == "Tasks");
        entries.Select(w => w.Field).Should().Contain(["Title", "State", "CreatedAt", "UpdatedAt"]);
    }

    /// <summary>
    /// Znacznik dopisany w tym samym kontekście, a jeszcze niezapisany, nie ma
    /// prowadzić do dopisania drugiego z tym samym kluczem.
    /// </summary>
    /// <remarks>
    /// Tak robi nakładanie zmian z synchronizacji: dokłada znaczniki i zostawia je
    /// do wspólnego zapisu. Zapytanie o znaczniki szło wtedy wyłącznie do bazy,
    /// w której ich jeszcze nie było — i zapis wywracał się na dwóch instancjach
    /// tego samego wiersza. Widać to było jako błąd przy zapisie zadania, choć
    /// z zadaniem nie miało nic wspólnego.
    /// </remarks>
    [Fact]
    public void Znacznik_dopisany_i_niezapisany_nie_powiela_sie_przy_zapisie()
    {
        var task = Save();

        // Notatka jest pusta przy zakładaniu, więc **nie ma jeszcze swojego znacznika** —
        // ani w bazie, ani w śledzeniu. To jest dokładnie ten stan, w którym nakładanie
        // zmian z synchronizacji dokłada znacznik i zostawia go do wspólnego zapisu.
        _db.Add(new FieldStamp("Tasks", task.Id, "Note", new Hlc(9999, 0, "telefon").ToString()));

        task.SetNote("z drugiego urządzenia", Stamp());

        // Bez poprawki leci tu wyjątek o instancji, której nie da się śledzić:
        // zapytanie o znaczniki szło wyłącznie do bazy i tego dopisanego nie widziało.
        _db.Invoking(baza => baza.SaveChanges()).Should().NotThrow();

        _db.FieldStamps.Count(z => z.EntityId == task.Id && z.Field == "Note")
            .Should().Be(1, "jeden znacznik na pole, niezależnie od tego, kto go dopisał");
    }

    [Fact]
    public void Klucz_glowny_nie_trafia_do_dziennika_jako_pole()
    {
        Save();

        _db.Changes.Select(w => w.Field).Should().NotContain("Id");
    }

    [Fact]
    public void Puste_pola_nowej_encji_nie_zasmiecaja_dziennika()
    {
        Save();

        _db.Changes.Select(w => w.Field).Should().NotContain(["Deadline", "WaitingForWho", "ProjectId"]);
    }

    [Fact]
    public void Zmiana_zapisuje_wylacznie_pola_faktycznie_zmienione()
    {
        var task = Save();
        var poDodaniu = _db.Changes.Count();

        task.Rename("Zadzwonić do przychodni po skierowanie", Stamp());
        _db.SaveChanges();

        var fresh = _db.Changes.Skip(poDodaniu).ToList();
        fresh.Select(w => w.Field).Should().BeEquivalentTo("Title", "UpdatedAt");
    }

    [Fact]
    public void Wpis_niesie_wartosc_w_postaci_bazodanowej()
    {
        Save("kupić mleko");

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
        Save("zażółć gęślą jaźń");

        var value = _db.Changes.Single(w => w.Field == "Title").Value;

        value.Should().NotContain("\\u");
        value.Should().Contain("zażółć gęślą jaźń");
    }

    [Fact]
    public void Znacznik_wpisu_jest_znacznikiem_encji()
    {
        var task = Save();

        _db.Changes.Should().OnlyContain(w => w.Hlc == task.UpdatedAt.ToString());
    }

    [Fact]
    public void Znaczniki_pol_powstaja_i_sa_podnoszone_przy_zmianie()
    {
        var task = Save();
        var tytulPrzed = _db.FieldStamps.Single(f => f.Field == "Title").Hlc;

        task.Rename("inny tytuł", Stamp());
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
        var task = Save();
        var stanPrzed = _db.FieldStamps.Single(f => f.Field == "State").Hlc;

        task.Rename("inny tytuł", Stamp());
        _db.SaveChanges();

        _db.FieldStamps.Single(f => f.Field == "State").Hlc.Should().Be(stanPrzed);
    }

    [Fact]
    public void Kazde_pole_ma_dokladnie_jeden_znacznik()
    {
        var task = Save();
        task.Rename("raz", Stamp());
        _db.SaveChanges();
        task.Rename("dwa", Stamp());
        _db.SaveChanges();

        _db.FieldStamps.Count(f => f.Field == "Title").Should().Be(1);
    }

    [Fact]
    public void Dziennik_nie_zapisuje_samego_siebie()
    {
        Save();

        _db.Changes.Select(w => w.EntityType).Should().NotContain(["Changes", "FieldStamps"]);
    }

    [Fact]
    public void Wpisy_zaczynaja_jako_niewyslane()
    {
        Save();

        _db.Changes.Should().OnlyContain(w => !w.Sent);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
