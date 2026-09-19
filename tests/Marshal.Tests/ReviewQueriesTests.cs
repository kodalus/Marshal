using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Review;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Review;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Niezmienniki z rozdziału 6 jako zapytania (spec 8.2, 8.5).
/// </summary>
public sealed class ReviewQueriesTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private static readonly DateOnly Dzis = new(2026, 9, 16);

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();
    private readonly HlcSource _hlc;
    private readonly ReviewQueries _zapytania;
    private double _kolejnosc;

    public ReviewQueriesTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_zegar, "biurko");
        _zapytania = new ReviewQueries(_db);
    }

    private Area Obszar(string name, int quietDays = 30, int nudgeDays = 7)
    {
        var area = new Area(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), name, _kolejnosc++, quietDays, nudgeDays);
        _db.Areas.Add(area);
        _db.SaveChanges();
        return area;
    }

    private Project Projekt(string result, Area area, Guid? parent = null)
    {
        var project = new Project(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), result, area.Id, _kolejnosc++, parent);
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private TaskItem TaskId(string title, Area area, Guid? project = null)
    {
        var task = TaskItem.Capture(title, _zegar.Now, _hlc.Next());
        task.MakeNext(area.Id, _hlc.Next());

        if (project is { } p)
        {
            task.MoveTo(area.Id, p, _hlc.Next());
        }

        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    // --- N1: projekty zablokowane (8.2) --------------------------------------

    [Fact]
    public async Task Projekt_bez_zadnej_akcji_jest_zablokowany()
    {
        var area = Obszar("Dom");
        Projekt("Zimowe opony są na aucie", area);

        var result = await _zapytania.BlockedProjectsAsync();

        result.Should().ContainSingle();
        result[0].Outcome.Should().Be("Zimowe opony są na aucie");
        result[0].AreaName.Should().Be("Dom");
    }

    [Fact]
    public async Task Projekt_z_nastepna_akcja_nie_jest_zablokowany()
    {
        var area = Obszar("Dom");
        var project = Projekt("Zimowe opony są na aucie", area);
        TaskId("Zadzwonić do wulkanizacji", area, project.Id);

        (await _zapytania.BlockedProjectsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projekt_ktorego_jedyna_akcja_to_oczekiwanie_nie_jest_zablokowany()
    {
        // Czekanie na kogoś jest prawidłowym stanem, a nie brakiem następnej akcji.
        var area = Obszar("Sprawy urzędowe");
        var project = Projekt("Wniosek jest rozpatrzony", area);
        var task = TaskId("Odpowiedź z urzędu", area, project.Id);
        task.Delegate(area.Id, "urząd", Dzis.AddDays(-3), null, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.BlockedProjectsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projekt_z_wykonana_akcja_i_niczym_wiecej_jest_zablokowany()
    {
        var area = Obszar("Dom");
        var project = Projekt("Zimowe opony są na aucie", area);
        var task = TaskId("Zadzwonić do wulkanizacji", area, project.Id);
        task.Complete(_zegar.Now, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.BlockedProjectsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Cel_z_zywym_podprojektem_nie_jest_zablokowany()
    {
        // Najważniejszy test tego zapytania. Cel („prawo jazdy") zwykle nie ma własnych
        // zadań — ma podprojekty, które je mają. Bez warunku o podprojektach każdy cel
        // zgłaszałby się jako zablokowany, N1 sypałby fałszywymi alarmami i przestałabyś
        // na niego patrzeć. Niezmiennik, który krzyczy bez powodu, uczy ignorowania.
        var area = Obszar("Rozwój własny");
        var cel = Projekt("Mam prawo jazdy", area);
        var podprojekt = Projekt("Kurs jest zaliczony", area, cel.Id);
        TaskId("Zapisać się na kurs", area, podprojekt.Id);

        (await _zapytania.BlockedProjectsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Pusty_podprojekt_zglasza_sie_sam_a_nie_jego_rodzic()
    {
        // Wskazanie ma trafiać tam, gdzie brakuje akcji, a nie w korzeń drzewa.
        var area = Obszar("Rozwój własny");
        var cel = Projekt("Mam prawo jazdy", area);
        Projekt("Kurs jest zaliczony", area, cel.Id);

        var result = await _zapytania.BlockedProjectsAsync();

        result.Should().ContainSingle();
        result[0].Outcome.Should().Be("Kurs jest zaliczony");
    }

    [Fact]
    public async Task Projekt_zakonczony_nie_jest_zablokowany()
    {
        var area = Obszar("Dom");
        var project = Projekt("Zimowe opony są na aucie", area);
        project.SetState(ProjectState.Done, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.BlockedProjectsAsync()).Should().BeEmpty();
    }

    // --- utknięcie na kimś (8.2) ---------------------------------------------

    [Fact]
    public async Task Projekt_w_ktorym_wszystko_czeka_dlugo_jest_zglaszany_osobno()
    {
        var area = Obszar("Sprawy urzędowe");
        var project = Projekt("Wniosek jest rozpatrzony", area);
        var task = TaskId("Odpowiedź z urzędu", area, project.Id);
        task.Delegate(area.Id, "urząd skarbowy", Dzis.AddDays(-45), null, _hlc.Next());
        _db.SaveChanges();

        var result = await _zapytania.StuckOnSomeoneAsync(Dzis, days: 30);

        result.Should().ContainSingle();
        result[0].Days.Should().Be(45);
        result[0].Who.Should().Be("urząd skarbowy");
    }

    [Fact]
    public async Task Projekt_z_jedna_wlasna_akcja_nie_utknal_na_nikim()
    {
        // Ma co robić, więc nie utknął — niezależnie od tego, jak długo wisi reszta.
        var area = Obszar("Sprawy urzędowe");
        var project = Projekt("Wniosek jest rozpatrzony", area);
        var czekajace = TaskId("Odpowiedź z urzędu", area, project.Id);
        czekajace.Delegate(area.Id, "urząd", Dzis.AddDays(-90), null, _hlc.Next());
        TaskId("Przygotować załączniki", area, project.Id);
        _db.SaveChanges();

        (await _zapytania.StuckOnSomeoneAsync(Dzis, days: 30)).Should().BeEmpty();
    }

    // --- N3: oczekiwane i ponaglenia -----------------------------------------

    [Fact]
    public async Task Oczekiwane_wracaja_najdluzej_czekajace_pierwsze()
    {
        var area = Obszar("Dom");
        foreach (var (title, days) in new[] { ("świeże", 1), ("stare", 30), ("średnie", 10) })
        {
            var task = TaskId(title, area);
            task.Delegate(area.Id, "ktoś", Dzis.AddDays(-days), null, _hlc.Next());
        }

        _db.SaveChanges();

        (await _zapytania.WaitingAsync(Dzis)).Select(w => w.Task.Title)
            .Should().Equal("stare", "średnie", "świeże");
    }

    [Fact]
    public async Task Prog_ponaglenia_bierze_sie_z_obszaru_gdy_zadanie_go_nie_ma()
    {
        // Siedem dni dla urzędu jest absurdalnie agresywne — urząd nie odpowiada
        // w tydzień. Dlatego próg jest per obszar, nie globalny (5.2).
        var urzad = Obszar("Sprawy urzędowe", nudgeDays: 21);
        var task = TaskId("Odpowiedź z urzędu", urzad);
        task.Delegate(urzad.Id, "urząd", Dzis.AddDays(-10), null, _hlc.Next());
        _db.SaveChanges();

        var result = (await _zapytania.WaitingAsync(Dzis)).Single();

        result.NudgeDays.Should().Be(21);
        result.NeedsNudge.Should().BeFalse();
    }

    [Fact]
    public async Task Prog_zadania_nadpisuje_prog_obszaru()
    {
        var urzad = Obszar("Sprawy urzędowe", nudgeDays: 21);
        var task = TaskId("Pilna odpowiedź", urzad);
        task.Delegate(urzad.Id, "urząd", Dzis.AddDays(-10), nudgeDays: 5, _hlc.Next());
        _db.SaveChanges();

        var result = (await _zapytania.WaitingAsync(Dzis)).Single();

        result.NudgeDays.Should().Be(5);
        result.NeedsNudge.Should().BeTrue();
    }

    // --- 8.5: równowaga obszarów ---------------------------------------------

    [Fact]
    public async Task Obszar_bez_zadnego_ruchu_ma_pustke_a_nie_zero()
    {
        // „Brak ruchu" to nie to samo co „ruch dzisiaj". Zero dni znaczyłoby, że coś
        // się działo — a nie działo się nic.
        Obszar("Relacje");

        var result = (await _zapytania.BalanceAsync(Dzis)).Single();

        result.DaysSinceMove.Should().BeNull();
        result.ActiveProjects.Should().Be(0);
        result.IsQuiet.Should().BeTrue();
    }

    [Fact]
    public async Task Obszar_z_dzisiejszym_ruchem_nie_milczy()
    {
        var area = Obszar("Dom");
        TaskId("Wymienić żarówkę", area);

        var result = (await _zapytania.BalanceAsync(Dzis)).Single();

        result.DaysSinceMove.Should().Be(0);
        result.IsQuiet.Should().BeFalse();
    }

    [Fact]
    public async Task Cisza_liczy_sie_od_ostatniego_ruchu_w_obszarze()
    {
        // Kolejność nie jest tu dowolna: zegar logiczny nigdy się nie cofa, więc zapisu
        // z przeszłości nie da się zrobić po zapisie z teraźniejszości — kolejny znacznik
        // dostałby i tak czas bieżący. Scenariusz musi biec tak, jak biegnie naprawdę:
        // najpierw dawno, potem dziś.
        _zegar.Now = _zegar.Now.AddDays(-100);
        var area = Obszar("Twórczość", quietDays: 45);
        TaskId("Nagrać pierwszą historię", area);
        _zegar.Now = _zegar.Now.AddDays(100);

        var result = (await _zapytania.BalanceAsync(Dzis)).Single();

        result.DaysSinceMove.Should().Be(100);
        result.IsQuiet.Should().BeTrue();
    }

    [Fact]
    public async Task Prog_ciszy_jest_wlasnoscia_obszaru_nie_stala()
    {
        // Praca i Dzieci mają progi, które praktycznie nigdy nie zadziałają, i tak ma
        // być: wartość mechanizmu leży cała w dolnej połowie tabeli (5.2).
        _zegar.Now = _zegar.Now.AddDays(-20);
        var work = Obszar("Praca", quietDays: 14);
        var tworczosc = Obszar("Twórczość", quietDays: 45);
        TaskId("Przegląd kodu", work);
        TaskId("Szkic historii", tworczosc);
        _zegar.Now = _zegar.Now.AddDays(20);

        var result = await _zapytania.BalanceAsync(Dzis);

        result.Single(b => b.Name == "Praca").IsQuiet.Should().BeTrue();
        result.Single(b => b.Name == "Twórczość").IsQuiet.Should().BeFalse();
    }

    [Fact]
    public async Task Tabela_idzie_od_najdluzej_milczacych()
    {
        _zegar.Now = _zegar.Now.AddDays(-5);
        var work = Obszar("Praca");
        Obszar("Relacje");
        TaskId("Przegląd kodu", work);
        _zegar.Now = _zegar.Now.AddDays(5);

        (await _zapytania.BalanceAsync(Dzis)).Select(b => b.Name)
            .Should().Equal("Relacje", "Praca");
    }

    [Fact]
    public async Task Obszar_nieaktywny_znika_z_tabeli()
    {
        var area = Obszar("Dawny");
        area.SetActive(false, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.BalanceAsync(Dzis)).Should().BeEmpty();
    }

    // --- N5, N6, krok 5 ------------------------------------------------------

    [Fact]
    public async Task Po_terminie_lapie_tylko_niewykonane()
    {
        var area = Obszar("Finanse");
        var przeterminowane = TaskId("Zapłacić ratę", area);
        przeterminowane.SetDeadline(Dzis.AddDays(-3), _hlc.Next());

        var zrobione = TaskId("Rozliczyć PIT", area);
        zrobione.SetDeadline(Dzis.AddDays(-5), _hlc.Next());
        zrobione.Complete(_zegar.Now, _hlc.Next());

        var dzisiejsze = TaskId("Przelew", area);
        dzisiejsze.SetDeadline(Dzis, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.OverdueAsync(Dzis)).Select(t => t.Title)
            .Should().Equal("Zapłacić ratę");
    }

    [Fact]
    public async Task Kiedys_moze_dojrzewa_gdy_minie_data_odlozenia()
    {
        var area = Obszar("Rozwój własny");
        var dojrzale = TaskId("Nauczyć się hiszpańskiego", area);
        dojrzale.Postpone(area.Id, Dzis.AddDays(-1), _hlc.Next());

        var niedojrzale = TaskId("Kupić fortepian", area);
        niedojrzale.Postpone(area.Id, Dzis.AddDays(30), _hlc.Next());

        var bezDaty = TaskId("Kiedyś Islandia", area);
        bezDaty.Postpone(area.Id, null, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.MaturedSomedayAsync(Dzis)).Select(t => t.Title)
            .Should().Equal("Nauczyć się hiszpańskiego");
    }

    [Fact]
    public async Task Projekty_nietkniete_od_dawna_trafiaja_do_kroku_piatego()
    {
        _zegar.Now = _zegar.Now.AddDays(-30);
        var area = Obszar("Dom");
        Projekt("Piwnica jest uporządkowana", area);
        _zegar.Now = _zegar.Now.AddDays(30);
        Projekt("Zimowe opony są na aucie", area);

        (await _zapytania.StaleProjectsAsync(Dzis, days: 14)).Select(p => p.Outcome)
            .Should().Equal("Piwnica jest uporządkowana");
    }

    [Fact]
    public async Task Skrzynka_zalega_dopiero_po_tygodniu()
    {
        _zegar.Now = _zegar.Now.AddDays(-10);
        _db.Tasks.Add(TaskItem.Capture("stary wrzut", _zegar.Now, _hlc.Next()));
        _zegar.Now = _zegar.Now.AddDays(10);
        _db.Tasks.Add(TaskItem.Capture("świeży wrzut", _zegar.Now, _hlc.Next()));
        _db.SaveChanges();

        (await _zapytania.StaleInboxCountAsync(Dzis)).Should().Be(1);
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}
