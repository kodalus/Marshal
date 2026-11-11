using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
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
/// Przełączenie ptaszka: kierunek bierze się z wiersza, nie z okna.
/// </summary>
/// <remarks>
/// <para>
/// Kwadracik w karcie zadania przełącza się sam przy kliknięciu, a wiązanie
/// jednokierunkowe poprawia go dopiero wtedy, gdy źródło powie, że coś się zmieniło —
/// i nie mówi, gdy nowo otwarte zadanie ma ten sam stan co poprzednie. Wystarczyło więc
/// odhaczyć cokolwiek z karty, żeby następne nieodhaczone zadanie pokazało ptaszka,
/// którego w danych nie ma.
/// </para>
/// <para>
/// Sam widok jest już na to odporny, ale to nie jest miejsce, w którym ta usterka ma
/// zostać zamknięta: <b>widok może kłamać o stanie, a czynność nie ma prawa od tego
/// kłamstwa zależeć</b>. Dlatego kierunek czyta się z wiersza — i dlatego da się to
/// sprawdzić bez okna.
/// </para>
/// <para>
/// Koszt tamtej usterki był cichy i duży: otwarte zadanie wyglądało na zrobione.
/// Zamknięte bez klikania zostawało nierobione, a przy wystąpieniu serii z rytmem
/// „przepada" przejście dnia zabierało je nazajutrz razem z dniem.
/// </para>
/// </remarks>
public sealed class ToggleDoneTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 11, 11, 9, 0, 0, TimeSpan.FromHours(1));
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly HlcSource _hlc;
    private readonly TaskEditService _edit;
    private readonly Guid _area = Guid.CreateVersion7();

    public ToggleDoneTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_clock, "telefon");

        _edit = new TaskEditService(
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _clock, new AreaRepository(_db),
            new NoTaskMirror(), new TaskSeriesRepository(_db),
            new SeriesService(
                new TaskSeriesRepository(_db), new TaskRepository(_db), new UnitOfWork(_db),
                _clock, _hlc));
    }

    private TaskItem Add(string title)
    {
        var task = TaskItem.Capture(title, _clock.Now, _hlc.Next());
        task.Schedule(_area, DateOnly.FromDateTime(_clock.Now.Date), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    private TaskState StateOf(Guid id) => _db.Tasks.Single(t => t.Id == id).State;

    [Fact]
    public async Task Zadanie_otwarte_zostaje_odhaczone()
    {
        var task = Add("Seweryn: do ortopedy");

        (await _edit.ToggleDoneAsync(task.Id)).Should().BeTrue();

        StateOf(task.Id).Should().Be(TaskState.Done);
        _db.Tasks.Single(t => t.Id == task.Id).CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Zadanie_odhaczone_dostaje_zdjety_ptaszek()
    {
        var task = Add("Seweryn: do ortopedy");
        await _edit.CompleteAsync(task.Id);

        (await _edit.ToggleDoneAsync(task.Id)).Should().BeFalse();

        StateOf(task.Id).Should().Be(TaskState.Scheduled);
    }

    [Fact]
    public async Task Dwa_przelaczenia_wracaja_do_punktu_wyjscia()
    {
        // Przełącznik ma być przełącznikiem. Dotąd dwa kliknięcia potrafiły zostawić
        // zadanie w tym samym stanie, w jakim zaczęło — ale przez to, że pierwsze
        // zrobiło czynność odwrotną do zamierzonej, a nie przez to, że się zniosły.
        var task = Add("Wziąć lek");

        (await _edit.ToggleDoneAsync(task.Id)).Should().BeTrue();
        (await _edit.ToggleDoneAsync(task.Id)).Should().BeFalse();

        StateOf(task.Id).Should().Be(TaskState.Scheduled);
    }

    [Fact]
    public async Task Powtorzone_odhaczenie_nie_jest_zdjeciem_ptaszka()
    {
        // Drugi kształt tej samej usterki: zadanie **odhaczone** pokazywane jako
        // nieodhaczone. Kliknięcie szło wtedy w CompleteAsync, czyli w czynność, która
        // na zadaniu już zrobionym nie zmienia nic widocznego — i wyglądało to jak
        // kliknięcie, które nie zadziałało.
        var task = Add("Wziąć lek");
        await _edit.CompleteAsync(task.Id);

        var kiedy = _db.Tasks.Single(t => t.Id == task.Id).CompletedAt;

        _clock.Now = _clock.Now.AddHours(2);

        (await _edit.ToggleDoneAsync(task.Id)).Should().BeFalse("drugie kliknięcie zdejmuje ptaszek");

        StateOf(task.Id).Should().Be(TaskState.Scheduled);
        kiedy.Should().NotBeNull();
    }

    [Fact]
    public async Task Wystapienie_serii_przelacza_sie_tak_samo()
    {
        // Przy serii odhaczenie dopełnia okno, a zdjęcie ptaszka nie ma go psuć.
        var first = Add("Wziąć lek");
        var rhythms = new SeriesService(
            new TaskSeriesRepository(_db), new TaskRepository(_db), new UnitOfWork(_db),
            _clock, _hlc);

        var one = await rhythms.StartAsync(first, new RecurrenceRule(RecurrenceKind.Daily));

        (await _edit.ToggleDoneAsync(first.Id)).Should().BeTrue();
        StateOf(first.Id).Should().Be(TaskState.Done);

        (await _edit.ToggleDoneAsync(first.Id)).Should().BeFalse();
        StateOf(first.Id).Should().Be(TaskState.Scheduled);

        _db.Tasks.Count(t => t.SeriesId == one.Id).Should().BeGreaterThan(1, "okno stoi dalej");
    }

    [Fact]
    public async Task Zadania_ktorego_nie_ma_nie_da_sie_przelaczyc()
    {
        (await _edit.ToggleDoneAsync(Guid.CreateVersion7())).Should().BeNull();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
