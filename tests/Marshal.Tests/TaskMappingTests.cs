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
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));

    public TaskMappingTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
    }

    private MarshalDbContext NewContext()
    {
        var context = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>().UseSqlite(_connection).Options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public void Zadanie_przechodzi_zapis_i_odczyt_bez_utraty_pol()
    {
        var area = Guid.CreateVersion7();
        var day = new DateOnly(2026, 9, 21);
        var deadline = new DateOnly(2026, 9, 30);
        Guid id;

        using (var patch = NewContext())
        {
            var task = TaskItem.Capture("Złożyć wniosek", Now, new Hlc(1000, 0, "a"));
            task.Schedule(area, day, new Hlc(2000, 0, "a"));
            task.SetDeadline(deadline, new Hlc(3000, 0, "a"));
            task.SetNote("Załączniki: **skan dowodu**", new Hlc(4000, 0, "a"));
            id = task.Id;
            patch.Tasks.Add(task);
            patch.SaveChanges();
        }

        using var read = NewContext();
        var wczytane = read.Tasks.Single();

        wczytane.Id.Should().Be(id);
        wczytane.Title.Should().Be("Złożyć wniosek");
        wczytane.State.Should().Be(TaskState.Scheduled);
        wczytane.AreaId.Should().Be(area);
        wczytane.DoDate.Should().Be(day);
        wczytane.Deadline.Should().Be(deadline);
        wczytane.Note.Should().Be("Załączniki: **skan dowodu**");
    }

    [Fact]
    public void Pozycja_w_skrzynce_zapisuje_sie_bez_obszaru()
    {
        using var context = NewContext();
        context.Tasks.Add(TaskItem.Capture("luźna myśl", Now, new Hlc(1000, 0, "a")));

        var save = () => context.SaveChanges();

        save.Should().NotThrow();
        context.Tasks.Single().AreaId.Should().BeNull();
    }

    [Fact]
    public void Stan_jest_skladowany_jako_liczba()
    {
        using var context = NewContext();
        var task = TaskItem.Capture("cokolwiek", Now, new Hlc(1000, 0, "a"));
        task.Postpone(Guid.CreateVersion7(), null, new Hlc(2000, 0, "a"));
        context.Tasks.Add(task);
        context.SaveChanges();

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT State FROM Tasks LIMIT 1";

        Convert.ToInt32(command.ExecuteScalar()).Should().Be((int)TaskState.Someday);
    }

    [Fact]
    public void Projekt_i_jego_zadania_wiaze_goly_identyfikator()
    {
        var area = Guid.CreateVersion7();
        var project = new Project(
            Guid.CreateVersion7(), Now, new Hlc(1000, 0, "a"), "Wniosek jest złożony", area, 1.0);

        using var context = NewContext();
        context.Projects.Add(project);

        var task = TaskItem.Capture("Zebrać dokumenty", Now, new Hlc(1000, 0, "a"));
        task.MakeNext(area, new Hlc(2000, 0, "a"));
        task.MoveTo(area, project.Id, new Hlc(3000, 0, "a"));
        context.Tasks.Add(task);
        context.SaveChanges();

        context.Tasks.Single(t => t.ProjectId == project.Id).Title.Should().Be("Zebrać dokumenty");
    }

    [Fact]
    public void Zadanie_zapisuje_sie_gdy_jego_projekt_jeszcze_nie_dotarl()
    {
        // Brak kluczy obcych jest celowy: przy synchronizacji zmiany przychodzą
        // w kolejności zapisu, nie zależności, więc zadanie potrafi wyprzedzić projekt.
        using var context = NewContext();
        var task = TaskItem.Capture("Krok projektu, który dopiero nadejdzie", Now, new Hlc(1000, 0, "a"));
        task.MakeNext(Guid.CreateVersion7(), new Hlc(2000, 0, "a"));
        task.MoveTo(task.AreaId!.Value, Guid.CreateVersion7(), new Hlc(3000, 0, "a"));
        context.Tasks.Add(task);

        var save = () => context.SaveChanges();

        save.Should().NotThrow();
    }

    public void Dispose() => _connection.Dispose();
}
