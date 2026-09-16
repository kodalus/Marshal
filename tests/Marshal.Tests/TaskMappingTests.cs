using FluentAssertions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

public sealed class TaskMappingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly DateTimeOffset Teraz = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));

    public TaskMappingTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
    }

    private MarshalDbContext Kontekst()
    {
        var kontekst = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>().UseSqlite(_connection).Options);
        kontekst.Database.EnsureCreated();
        return kontekst;
    }

    [Fact]
    public void Zadanie_przechodzi_zapis_i_odczyt_bez_utraty_pol()
    {
        var obszar = Guid.CreateVersion7();
        var dzien = new DateOnly(2026, 9, 21);
        var termin = new DateOnly(2026, 9, 30);
        Guid id;

        using (var zapis = Kontekst())
        {
            var zadanie = TaskItem.Capture("Złożyć wniosek", Teraz, new Hlc(1000, 0, "a"));
            zadanie.Schedule(obszar, dzien, new Hlc(2000, 0, "a"));
            zadanie.SetDeadline(termin, new Hlc(3000, 0, "a"));
            zadanie.SetNote("Załączniki: **skan dowodu**", new Hlc(4000, 0, "a"));
            id = zadanie.Id;
            zapis.Tasks.Add(zadanie);
            zapis.SaveChanges();
        }

        using var odczyt = Kontekst();
        var wczytane = odczyt.Tasks.Single();

        wczytane.Id.Should().Be(id);
        wczytane.Title.Should().Be("Złożyć wniosek");
        wczytane.State.Should().Be(TaskState.Scheduled);
        wczytane.AreaId.Should().Be(obszar);
        wczytane.DoDate.Should().Be(dzien);
        wczytane.Deadline.Should().Be(termin);
        wczytane.Note.Should().Be("Załączniki: **skan dowodu**");
    }

    [Fact]
    public void Pozycja_w_skrzynce_zapisuje_sie_bez_obszaru()
    {
        using var kontekst = Kontekst();
        kontekst.Tasks.Add(TaskItem.Capture("luźna myśl", Teraz, new Hlc(1000, 0, "a")));

        var zapisz = () => kontekst.SaveChanges();

        zapisz.Should().NotThrow();
        kontekst.Tasks.Single().AreaId.Should().BeNull();
    }

    [Fact]
    public void Stan_jest_skladowany_jako_liczba()
    {
        using var kontekst = Kontekst();
        var zadanie = TaskItem.Capture("cokolwiek", Teraz, new Hlc(1000, 0, "a"));
        zadanie.Postpone(Guid.CreateVersion7(), null, new Hlc(2000, 0, "a"));
        kontekst.Tasks.Add(zadanie);
        kontekst.SaveChanges();

        using var polecenie = _connection.CreateCommand();
        polecenie.CommandText = "SELECT State FROM Tasks LIMIT 1";

        Convert.ToInt32(polecenie.ExecuteScalar()).Should().Be((int)TaskState.Someday);
    }

    [Fact]
    public void Projekt_i_jego_zadania_wiaze_goly_identyfikator()
    {
        var obszar = Guid.CreateVersion7();
        var projekt = new Project(
            Guid.CreateVersion7(), Teraz, new Hlc(1000, 0, "a"), "Wniosek jest złożony", obszar, 1.0);

        using var kontekst = Kontekst();
        kontekst.Projects.Add(projekt);

        var zadanie = TaskItem.Capture("Zebrać dokumenty", Teraz, new Hlc(1000, 0, "a"));
        zadanie.MakeNext(obszar, new Hlc(2000, 0, "a"));
        zadanie.MoveTo(obszar, projekt.Id, new Hlc(3000, 0, "a"));
        kontekst.Tasks.Add(zadanie);
        kontekst.SaveChanges();

        kontekst.Tasks.Single(t => t.ProjectId == projekt.Id).Title.Should().Be("Zebrać dokumenty");
    }

    [Fact]
    public void Zadanie_zapisuje_sie_gdy_jego_projekt_jeszcze_nie_dotarl()
    {
        // Brak kluczy obcych jest celowy: przy synchronizacji zmiany przychodzą
        // w kolejności zapisu, nie zależności, więc zadanie potrafi wyprzedzić projekt.
        using var kontekst = Kontekst();
        var zadanie = TaskItem.Capture("Krok projektu, który dopiero nadejdzie", Teraz, new Hlc(1000, 0, "a"));
        zadanie.MakeNext(Guid.CreateVersion7(), new Hlc(2000, 0, "a"));
        zadanie.MoveTo(zadanie.AreaId!.Value, Guid.CreateVersion7(), new Hlc(3000, 0, "a"));
        kontekst.Tasks.Add(zadanie);

        var zapisz = () => kontekst.SaveChanges();

        zapisz.Should().NotThrow();
    }

    public void Dispose() => _connection.Dispose();
}
