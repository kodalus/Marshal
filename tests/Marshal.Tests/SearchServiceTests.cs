using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Notes;
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
/// Szukanie po całej aplikacji: nazwy i treści zadań oraz notatek.
/// </summary>
/// <remarks>
/// Szuka się rzeczy, o których pamięta się tyle, że były — więc testy pilnują przede
/// wszystkim zasięgu: treść, nie tylko nazwa; zrobione i wyrzucone, nie tylko otwarte.
/// Szukanie, które pokazuje wyłącznie żywe zadania, odpowiada „nie ma" na pytanie
/// „gdzie ja to zapisałam".
/// </remarks>
public sealed class SearchServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 10, 3, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly HlcSource _hlc;
    private readonly SearchService _search;

    public SearchServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();

        _hlc = new HlcSource(_clock, "biurko");
        _search = new SearchService(new TaskRepository(_db), new NoteRepository(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TaskItem Task(string title, string? note = null)
    {
        var task = TaskItem.Capture(title, _clock.Now, _hlc.Next());

        if (note is not null)
        {
            task.SetNote(note, _hlc.Next());
        }

        _db.Tasks.Add(task);
        _db.SaveChanges();

        return task;
    }

    private Note Jot(string title, string content)
    {
        var note = Note.Create(title, _clock.Now, _hlc.Next());
        note.SetContent(content, _hlc.Next());
        _db.Notes.Add(note);
        _db.SaveChanges();

        return note;
    }

    [Fact]
    public async Task Szuka_w_nazwie_i_w_tresci_zadania()
    {
        Task("Zadzwonić do przychodni");
        Task("Kupić prezent", note: "Dla mamy, coś na działkę — przychodnia odpada");
        Task("Zupełnie co innego");

        var hits = await _search.FindAsync("przychodn");

        hits.Tasks.Select(z => z.Title)
            .Should().BeEquivalentTo(["Zadzwonić do przychodni", "Kupić prezent"]);
    }

    [Fact]
    public async Task Szuka_w_notatkach_i_oddaje_je_osobno()
    {
        Task("Zadzwonić do przychodni");
        Jot("Zdrowie", "Numer do przychodni: 123 456 789");
        Jot("Zakupy", "mleko, chleb");

        var hits = await _search.FindAsync("przychodni");

        hits.Tasks.Should().ContainSingle();
        hits.Notes.Should().ContainSingle().Which.Title.Should().Be("Zdrowie");
        hits.Count.Should().Be(2);
    }

    [Fact]
    public async Task Znajduje_takze_zrobione_i_wyrzucone()
    {
        // Sedno: szuka się rzeczy, o których pamięta się tyle, że były. „Było" znaczy
        // najczęściej, że już nie wisi na żadnej liście.
        var done = Task("Wizyta w przychodni");
        done.Complete(_clock.Now, _hlc.Next());

        var gone = Task("Przychodnia — odwołane");
        gone.Trash(_hlc.Next());

        _db.SaveChanges();

        var hits = await _search.FindAsync("przychodni");

        hits.Tasks.Should().HaveCount(2);
        hits.Tasks.Should().Contain(z => z.State == TaskState.Done);
        hits.Tasks.Should().Contain(z => z.State == TaskState.Trashed);
    }

    [Fact]
    public async Task Otwarte_stoja_przed_zamknietymi()
    {
        var done = Task("Przychodnia raz");
        done.Complete(_clock.Now, _hlc.Next());
        _db.SaveChanges();

        Task("Przychodnia dwa");

        var hits = await _search.FindAsync("Przychodnia");

        hits.Tasks[0].Title.Should().Be("Przychodnia dwa", "żywe pierwsze, zamknięte po nich");
        hits.Tasks[1].Title.Should().Be("Przychodnia raz");
    }

    [Fact]
    public async Task Puste_pytanie_oddaje_pustke_a_nie_cala_baze()
    {
        Task("Cokolwiek");
        Jot("Notatka", "treść");

        (await _search.FindAsync(null)).Count.Should().Be(0);
        (await _search.FindAsync("   ")).Count.Should().Be(0);
    }

    [Fact]
    public async Task Znaki_zapisu_szukania_sa_zwyklymi_znakami()
    {
        // Procent w LIKE znaczy „cokolwiek", a podkreślenie „jeden dowolny znak".
        // Wpisane w pole szukania mają być tym, co ktoś wpisał — sam procent oddawał
        // wszystko, a „100_" znajdowało „100 netto" zamiast „100_netto".
        Task("Rabat 100% na kurs");
        Task("Kurs bez rabatu");
        Task("Plik 100_netto.pdf");
        Task("Kwota 100 netto");

        (await _search.FindAsync("%")).Tasks
            .Should().ContainSingle().Which.Title.Should().Be("Rabat 100% na kurs");

        (await _search.FindAsync("100_")).Tasks
            .Should().ContainSingle().Which.Title.Should().Be("Plik 100_netto.pdf");
    }

    [Fact]
    public async Task Zadanie_i_notatka_skasowane_nagrobkiem_nie_wracaja()
    {
        // Nagrobek znaczy „tego nie ma" i szukanie ma to uszanować. Kosz to co innego:
        // tam rzecz nadal jest, tylko odłożona.
        var task = Task("Przychodnia skasowana");
        task.MarkDeleted(_hlc.Next());

        var note = Jot("Przychodnia", "skasowana");
        note.MarkDeleted(_hlc.Next());

        _db.SaveChanges();

        (await _search.FindAsync("przychodni")).Count.Should().Be(0);
    }
}
