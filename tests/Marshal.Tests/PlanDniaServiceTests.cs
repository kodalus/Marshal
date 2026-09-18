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
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 18, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();
    private readonly HlcSource _hlc;
    private readonly PlanDniaService _plan;
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

        _plan = new PlanDniaService(
            new TaskRepository(_db), new ProjectRepository(_db), new AreaRepository(_db), _zegar);
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }

    private DateOnly Dzis => ((IClock)_zegar).Today;

    private TaskItem Dodaj(string tytul, DateOnly? dzien = null, TimeOnly? pora = null)
    {
        var zadanie = TaskItem.Capture(tytul, _zegar.Now, _hlc.Next());
        zadanie.Schedule(_obszar.Id, dzien ?? Dzis, _hlc.Next());

        if (pora is { } godzina)
        {
            zadanie.SetDoTime(godzina, _hlc.Next());
        }

        _db.Tasks.Add(zadanie);
        _db.SaveChanges();
        return zadanie;
    }

    [Fact]
    public async Task Umowione_ida_po_godzinach_a_bezgodzinne_na_koniec()
    {
        // Godzina jest jedyną rzeczą, która narzuca dniowi porządek z zewnątrz.
        Dodaj("Bez godziny");
        Dodaj("Zebranie", pora: new TimeOnly(16, 0));
        Dodaj("Zakupy", pora: new TimeOnly(8, 0));

        var wiersze = await _plan.DzisAsync();

        wiersze.Select(w => w.Tytul).Should().Equal("Zakupy", "Zebranie", "Bez godziny");
    }

    [Fact]
    public async Task Odhaczone_nie_stoi_w_planie()
    {
        // Plan odpowiada na pytanie „co jeszcze przede mną", nie „co dziś było".
        var zrobione = Dodaj("Zakupy", pora: new TimeOnly(8, 0));
        zrobione.Complete(_zegar.Now, _hlc.Next());
        _db.SaveChanges();

        Dodaj("Zebranie", pora: new TimeOnly(16, 0));

        var wiersze = await _plan.DzisAsync();

        wiersze.Select(w => w.Tytul).Should().Equal("Zebranie");
    }

    [Fact]
    public async Task Wziete_na_dzis_stoi_w_planie_raz()
    {
        // Zadanie umówione na dziś i **jednocześnie** wzięte na dziś wychodzi z dwóch
        // zapytań. Bez odsiewu po identyfikatorze stałoby w planie dwa razy.
        var oba = Dodaj("Zebranie", pora: new TimeOnly(16, 0));
        oba.Focus(Dzis, _hlc.Next());

        var samWybor = TaskItem.Capture("Zadzwonić", _zegar.Now, _hlc.Next());
        samWybor.Focus(Dzis, _hlc.Next());
        _db.Tasks.Add(samWybor);
        _db.SaveChanges();

        var wiersze = await _plan.DzisAsync();

        wiersze.Select(w => w.Tytul).Should().Equal("Zebranie", "Zadzwonić");
        wiersze.Single(w => w.Tytul == "Zadzwonić").Podpis.Should().Be("wzięte na dziś");
    }

    [Fact]
    public async Task Zalegle_mowi_od_kiedy_zamiast_udawac_dzisiejsza_godzine()
    {
        // Zaległe ma godzinę sprzed paru dni. Wstawiona między dzisiejsze udawałaby,
        // że jest na nią umówione dziś — stąd osobny podpis i miejsce na końcu.
        Dodaj("Rozliczenie", dzien: Dzis.AddDays(-3), pora: new TimeOnly(7, 0));
        Dodaj("Zebranie", pora: new TimeOnly(16, 0));

        var wiersze = await _plan.DzisAsync();

        wiersze.Select(w => w.Tytul).Should().Equal("Zebranie", "Rozliczenie");
        wiersze.Single(w => w.Tytul == "Rozliczenie").Podpis.Should().StartWith("zaległe z 15.09");
    }

    [Fact]
    public async Task Podpis_niesie_godziny_i_przynaleznosc()
    {
        var zadanie = Dodaj("Zebranie", pora: new TimeOnly(16, 0));
        zadanie.SetEstimate(30, Energy.Medium, _hlc.Next());
        _db.SaveChanges();

        var wiersz = (await _plan.DzisAsync()).Single();

        wiersz.Podpis.Should().Be("16:00 – 16:30 / Dom");
    }

    [Fact]
    public async Task Bez_oszacowania_zostaje_sama_godzina_rozpoczecia()
    {
        // Zgadywanie długości zrobiłoby z planu harmonogram, którego nikt nie ustalał.
        Dodaj("Zebranie", pora: new TimeOnly(16, 0));

        (await _plan.DzisAsync()).Single().Podpis.Should().Be("16:00 / Dom");
    }

    [Fact]
    public async Task Barwa_schodzi_z_obszaru_gdy_zadanie_swojej_nie_ma()
    {
        _obszar.SetColor("#FF8800", _hlc.Next());
        _db.SaveChanges();

        Dodaj("Zebranie", pora: new TimeOnly(16, 0));

        (await _plan.DzisAsync()).Single().Barwa.Should().Be("#FF8800");
    }

    [Fact]
    public async Task Pusty_dzien_daje_pusty_plan()
    {
        (await _plan.DzisAsync()).Should().BeEmpty();
    }
}
