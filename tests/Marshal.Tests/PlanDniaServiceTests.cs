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
public sealed class PlanDniaServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 18, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _zegar = new();
    private readonly HlcSource _hlc;
    private readonly DayPlanService _plan;
    private readonly Area _obszar;

    public PlanDniaServiceTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_zegar, "telefon");

        _obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(_obszar);
        _db.SaveChanges();

        _plan = new DayPlanService(
            new TaskRepository(_db), new ProjectRepository(_db), new AreaRepository(_db), _zegar);
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }

    private DateOnly Dzis => ((IClock)_zegar).Today;

    private TaskItem Add(string title, DateOnly? day = null, TimeOnly? time = null)
    {
        var task = TaskItem.Capture(title, _zegar.Now, _hlc.Next());
        task.Schedule(_obszar.Id, day ?? Dzis, _hlc.Next());

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
        var zrobione = Add("Zakupy", time: new TimeOnly(8, 0));
        zrobione.Complete(_zegar.Now, _hlc.Next());
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
        oba.Focus(Dzis, _hlc.Next());

        var samWybor = TaskItem.Capture("Zadzwonić", _zegar.Now, _hlc.Next());
        samWybor.Focus(Dzis, _hlc.Next());
        _db.Tasks.Add(samWybor);
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
        Add("Rozliczenie", day: Dzis.AddDays(-3), time: new TimeOnly(7, 0));
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
        _obszar.SetColor("#FF8800", _hlc.Next());
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
        Add("Jutrzejsze", day: Dzis.AddDays(1), time: new TimeOnly(10, 0));

        var jutro = await _plan.ForDayAsync(Dzis.AddDays(1));

        jutro.Select(w => w.Title).Should().Equal("Jutrzejsze");
        jutro.Single().Caption.Should().Be("10:00 / Dom");
    }

    [Fact]
    public async Task Zalegle_naleza_do_dzisiaj_a_nie_do_kazdego_ogladanego_dnia()
    {
        // Zaległe należą do dziś, bo to dziś trzeba z nimi coś zrobić. Dołożone do
        // czwartku udawałyby, że ktoś je na czwartek zaplanował.
        Add("Rozliczenie", day: Dzis.AddDays(-3), time: new TimeOnly(7, 0));

        (await _plan.TodayAsync()).Select(w => w.Title).Should().Equal("Rozliczenie");
        (await _plan.ForDayAsync(Dzis.AddDays(2))).Should().BeEmpty();
    }

    [Fact]
    public async Task Wziete_na_inny_dzien_widac_w_tamtym_dniu()
    {
        var task = TaskItem.Capture("Zadzwonić", _zegar.Now, _hlc.Next());
        task.Focus(Dzis.AddDays(1), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        (await _plan.TodayAsync()).Should().BeEmpty();

        var jutro = await _plan.ForDayAsync(Dzis.AddDays(1));

        jutro.Single().Caption.Should().Be("wzięte na ten dzień");
    }
}
