using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
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
/// Plan dnia — treść i kolejność tego, co widać w widgecie.
/// </summary>
/// <remarks>
/// Widgetu nie da się uruchomić w teście: jego wiersze rozwija system w procesie
/// ekranu domowego. Dlatego cała treść planu liczy się poza nim i dlatego jest tu
/// sprawdzana — inaczej jedyną drogą byłoby patrzenie na telefon i zgadywanie.
/// </remarks>
public sealed class DayPlanServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 18, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly HlcSource _hlc;
    private readonly DayPlanService _plan;
    private readonly Area _area;

    public DayPlanServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_clock, "telefon");

        _area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(_area);
        _db.SaveChanges();

        _plan = new DayPlanService(
            new TaskRepository(_db), new ProjectRepository(_db), new AreaRepository(_db), _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private DateOnly Today => ((IClock)_clock).Today;

    private TaskItem Add(string title, DateOnly? day = null, TimeOnly? time = null)
    {
        var task = TaskItem.Capture(title, _clock.Now, _hlc.Next());
        task.Schedule(_area.Id, day ?? Today, _hlc.Next());

        if (time is { } hour)
        {
            task.SetDoTime(hour, _hlc.Next());
        }

        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    [Fact]
    public async Task Umowione_ida_po_godzinach_a_bezgodzinne_na_koniec()
    {
        // Godzina jest jedyną rzeczą, która narzuca dniowi porządek z zewnątrz.
        Add("Bez godziny");
        Add("Zebranie", time: new TimeOnly(16, 0));
        Add("Zakupy", time: new TimeOnly(8, 0));

        var rows = await _plan.TodayAsync();

        rows.Select(w => w.Title).Should().Equal("Zakupy", "Zebranie", "Bez godziny");
    }

    [Fact]
    public async Task Odhaczone_nie_stoi_w_planie()
    {
        // Plan odpowiada na pytanie „co jeszcze przede mną", nie „co dziś było".
        var done = Add("Zakupy", time: new TimeOnly(8, 0));
        done.Complete(_clock.Now, _hlc.Next());
        _db.SaveChanges();

        Add("Zebranie", time: new TimeOnly(16, 0));

        var rows = await _plan.TodayAsync();

        rows.Select(w => w.Title).Should().Equal("Zebranie");
    }

    [Fact]
    public async Task Wziete_na_dzis_stoi_w_planie_raz()
    {
        // Zadanie umówione na dziś i **jednocześnie** wzięte na dziś wychodzi z dwóch
        // zapytań. Bez odsiewu po identyfikatorze stałoby w planie dwa razy.
        var oba = Add("Zebranie", time: new TimeOnly(16, 0));
        oba.Focus(Today, _hlc.Next());

        var choiceOnly = TaskItem.Capture("Zadzwonić", _clock.Now, _hlc.Next());
        choiceOnly.Focus(Today, _hlc.Next());
        _db.Tasks.Add(choiceOnly);
        _db.SaveChanges();

        var rows = await _plan.TodayAsync();

        rows.Select(w => w.Title).Should().Equal("Zebranie", "Zadzwonić");
        rows.Single(w => w.Title == "Zadzwonić").Caption.Should().Be("wzięte na dziś");
    }

    [Fact]
    public async Task Zalegle_mowi_od_kiedy_zamiast_udawac_dzisiejsza_godzine()
    {
        // Zaległe ma godzinę sprzed paru dni. Wstawiona między dzisiejsze udawałaby,
        // że jest na nią umówione dziś — stąd osobny podpis i miejsce na końcu.
        Add("Rozliczenie", day: Today.AddDays(-3), time: new TimeOnly(7, 0));
        Add("Zebranie", time: new TimeOnly(16, 0));

        var rows = await _plan.TodayAsync();

        rows.Select(w => w.Title).Should().Equal("Zebranie", "Rozliczenie");
        rows.Single(w => w.Title == "Rozliczenie").Caption.Should().StartWith("zaległe z 15.09");
    }

    [Fact]
    public async Task Podpis_niesie_godziny_i_przynaleznosc()
    {
        var task = Add("Zebranie", time: new TimeOnly(16, 0));
        task.SetEstimate(30, Energy.Medium, _hlc.Next());
        _db.SaveChanges();

        var row = (await _plan.TodayAsync()).Single();

        row.Caption.Should().Be("16:00 – 16:30 / Dom");
    }

    [Fact]
    public async Task Bez_oszacowania_zostaje_sama_godzina_rozpoczecia()
    {
        // Zgadywanie długości zrobiłoby z planu harmonogram, którego nikt nie ustalał.
        Add("Zebranie", time: new TimeOnly(16, 0));

        (await _plan.TodayAsync()).Single().Caption.Should().Be("16:00 / Dom");
    }

    [Fact]
    public async Task Barwa_schodzi_z_obszaru_gdy_zadanie_swojej_nie_ma()
    {
        _area.SetColor("#FF8800", _hlc.Next());
        _db.SaveChanges();

        Add("Zebranie", time: new TimeOnly(16, 0));

        (await _plan.TodayAsync()).Single().Color.Should().Be("#FF8800");
    }

    [Fact]
    public async Task Pusty_dzien_daje_pusty_plan()
    {
        (await _plan.TodayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Jutro_pokazuje_swoje_zadania_a_nie_dzisiejsze()
    {
        Add("Dzisiejsze", time: new TimeOnly(9, 0));
        Add("Jutrzejsze", day: Today.AddDays(1), time: new TimeOnly(10, 0));

        var jutro = await _plan.ForDayAsync(Today.AddDays(1));

        jutro.Select(w => w.Title).Should().Equal("Jutrzejsze");
        jutro.Single().Caption.Should().Be("10:00 / Dom");
    }

    [Fact]
    public async Task Zalegle_naleza_do_dzisiaj_a_nie_do_kazdego_ogladanego_dnia()
    {
        // Zaległe należą do dziś, bo to dziś trzeba z nimi coś zrobić. Dołożone do
        // czwartku udawałyby, że ktoś je na czwartek zaplanował.
        Add("Rozliczenie", day: Today.AddDays(-3), time: new TimeOnly(7, 0));

        (await _plan.TodayAsync()).Select(w => w.Title).Should().Equal("Rozliczenie");
        (await _plan.ForDayAsync(Today.AddDays(2))).Should().BeEmpty();
    }

    [Fact]
    public async Task Wziete_na_inny_dzien_widac_w_tamtym_dniu()
    {
        var task = TaskItem.Capture("Zadzwonić", _clock.Now, _hlc.Next());
        task.Focus(Today.AddDays(1), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        (await _plan.TodayAsync()).Should().BeEmpty();

        var jutro = await _plan.ForDayAsync(Today.AddDays(1));

        jutro.Single().Caption.Should().Be("wzięte na ten dzień");
    }
}
