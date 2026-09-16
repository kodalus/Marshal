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

    private Area Obszar(string nazwa, int quietDays = 30, int nudgeDays = 7)
    {
        var obszar = new Area(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), nazwa, _kolejnosc++, quietDays, nudgeDays);
        _db.Areas.Add(obszar);
        _db.SaveChanges();
        return obszar;
    }

    private Project Projekt(string wynik, Area obszar, Guid? rodzic = null)
    {
        var projekt = new Project(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), wynik, obszar.Id, _kolejnosc++, rodzic);
        _db.Projects.Add(projekt);
        _db.SaveChanges();
        return projekt;
    }

    private TaskItem Zadanie(string tytul, Area obszar, Guid? projekt = null)
    {
        var zadanie = TaskItem.Capture(tytul, _zegar.Now, _hlc.Next());
        zadanie.MakeNext(obszar.Id, _hlc.Next());

        if (projekt is { } p)
        {
            zadanie.MoveTo(obszar.Id, p, _hlc.Next());
        }

        _db.Tasks.Add(zadanie);
        _db.SaveChanges();
        return zadanie;
    }

    // --- N1: projekty zablokowane (8.2) --------------------------------------

    [Fact]
    public async Task Projekt_bez_zadnej_akcji_jest_zablokowany()
    {
        var obszar = Obszar("Dom");
        Projekt("Zimowe opony są na aucie", obszar);

        var wynik = await _zapytania.BlockedProjectsAsync();

        wynik.Should().ContainSingle();
        wynik[0].Outcome.Should().Be("Zimowe opony są na aucie");
        wynik[0].AreaName.Should().Be("Dom");
    }

    [Fact]
    public async Task Projekt_z_nastepna_akcja_nie_jest_zablokowany()
    {
        var obszar = Obszar("Dom");
        var projekt = Projekt("Zimowe opony są na aucie", obszar);
        Zadanie("Zadzwonić do wulkanizacji", obszar, projekt.Id);

        (await _zapytania.BlockedProjectsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projekt_ktorego_jedyna_akcja_to_oczekiwanie_nie_jest_zablokowany()
    {
        // Czekanie na kogoś jest prawidłowym stanem, a nie brakiem następnej akcji.
        var obszar = Obszar("Sprawy urzędowe");
        var projekt = Projekt("Wniosek jest rozpatrzony", obszar);
        var zadanie = Zadanie("Odpowiedź z urzędu", obszar, projekt.Id);
        zadanie.Delegate(obszar.Id, "urząd", Dzis.AddDays(-3), null, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.BlockedProjectsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projekt_z_wykonana_akcja_i_niczym_wiecej_jest_zablokowany()
    {
        var obszar = Obszar("Dom");
        var projekt = Projekt("Zimowe opony są na aucie", obszar);
        var zadanie = Zadanie("Zadzwonić do wulkanizacji", obszar, projekt.Id);
        zadanie.Complete(_zegar.Now, _hlc.Next());
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
        var obszar = Obszar("Rozwój własny");
        var cel = Projekt("Mam prawo jazdy", obszar);
        var podprojekt = Projekt("Kurs jest zaliczony", obszar, cel.Id);
        Zadanie("Zapisać się na kurs", obszar, podprojekt.Id);

        (await _zapytania.BlockedProjectsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Pusty_podprojekt_zglasza_sie_sam_a_nie_jego_rodzic()
    {
        // Wskazanie ma trafiać tam, gdzie brakuje akcji, a nie w korzeń drzewa.
        var obszar = Obszar("Rozwój własny");
        var cel = Projekt("Mam prawo jazdy", obszar);
        Projekt("Kurs jest zaliczony", obszar, cel.Id);

        var wynik = await _zapytania.BlockedProjectsAsync();

        wynik.Should().ContainSingle();
        wynik[0].Outcome.Should().Be("Kurs jest zaliczony");
    }

    [Fact]
    public async Task Projekt_zakonczony_nie_jest_zablokowany()
    {
        var obszar = Obszar("Dom");
        var projekt = Projekt("Zimowe opony są na aucie", obszar);
        projekt.SetState(ProjectState.Done, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.BlockedProjectsAsync()).Should().BeEmpty();
    }

    // --- utknięcie na kimś (8.2) ---------------------------------------------

    [Fact]
    public async Task Projekt_w_ktorym_wszystko_czeka_dlugo_jest_zglaszany_osobno()
    {
        var obszar = Obszar("Sprawy urzędowe");
        var projekt = Projekt("Wniosek jest rozpatrzony", obszar);
        var zadanie = Zadanie("Odpowiedź z urzędu", obszar, projekt.Id);
        zadanie.Delegate(obszar.Id, "urząd skarbowy", Dzis.AddDays(-45), null, _hlc.Next());
        _db.SaveChanges();

        var wynik = await _zapytania.StuckOnSomeoneAsync(Dzis, days: 30);

        wynik.Should().ContainSingle();
        wynik[0].Days.Should().Be(45);
        wynik[0].Who.Should().Be("urząd skarbowy");
    }

    [Fact]
    public async Task Projekt_z_jedna_wlasna_akcja_nie_utknal_na_nikim()
    {
        // Ma co robić, więc nie utknął — niezależnie od tego, jak długo wisi reszta.
        var obszar = Obszar("Sprawy urzędowe");
        var projekt = Projekt("Wniosek jest rozpatrzony", obszar);
        var czekajace = Zadanie("Odpowiedź z urzędu", obszar, projekt.Id);
        czekajace.Delegate(obszar.Id, "urząd", Dzis.AddDays(-90), null, _hlc.Next());
        Zadanie("Przygotować załączniki", obszar, projekt.Id);
        _db.SaveChanges();

        (await _zapytania.StuckOnSomeoneAsync(Dzis, days: 30)).Should().BeEmpty();
    }

    // --- N3: oczekiwane i ponaglenia -----------------------------------------

    [Fact]
    public async Task Oczekiwane_wracaja_najdluzej_czekajace_pierwsze()
    {
        var obszar = Obszar("Dom");
        foreach (var (tytul, dni) in new[] { ("świeże", 1), ("stare", 30), ("średnie", 10) })
        {
            var zadanie = Zadanie(tytul, obszar);
            zadanie.Delegate(obszar.Id, "ktoś", Dzis.AddDays(-dni), null, _hlc.Next());
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
        var zadanie = Zadanie("Odpowiedź z urzędu", urzad);
        zadanie.Delegate(urzad.Id, "urząd", Dzis.AddDays(-10), null, _hlc.Next());
        _db.SaveChanges();

        var wynik = (await _zapytania.WaitingAsync(Dzis)).Single();

        wynik.NudgeDays.Should().Be(21);
        wynik.NeedsNudge.Should().BeFalse();
    }

    [Fact]
    public async Task Prog_zadania_nadpisuje_prog_obszaru()
    {
        var urzad = Obszar("Sprawy urzędowe", nudgeDays: 21);
        var zadanie = Zadanie("Pilna odpowiedź", urzad);
        zadanie.Delegate(urzad.Id, "urząd", Dzis.AddDays(-10), nudgeDays: 5, _hlc.Next());
        _db.SaveChanges();

        var wynik = (await _zapytania.WaitingAsync(Dzis)).Single();

        wynik.NudgeDays.Should().Be(5);
        wynik.NeedsNudge.Should().BeTrue();
    }

    // --- 8.5: równowaga obszarów ---------------------------------------------

    [Fact]
    public async Task Obszar_bez_zadnego_ruchu_ma_pustke_a_nie_zero()
    {
        // „Brak ruchu" to nie to samo co „ruch dzisiaj". Zero dni znaczyłoby, że coś
        // się działo — a nie działo się nic.
        Obszar("Relacje");

        var wynik = (await _zapytania.BalanceAsync(Dzis)).Single();

        wynik.DaysSinceMove.Should().BeNull();
        wynik.ActiveProjects.Should().Be(0);
        wynik.IsQuiet.Should().BeTrue();
    }

    [Fact]
    public async Task Obszar_z_dzisiejszym_ruchem_nie_milczy()
    {
        var obszar = Obszar("Dom");
        Zadanie("Wymienić żarówkę", obszar);

        var wynik = (await _zapytania.BalanceAsync(Dzis)).Single();

        wynik.DaysSinceMove.Should().Be(0);
        wynik.IsQuiet.Should().BeFalse();
    }

    [Fact]
    public async Task Cisza_liczy_sie_od_ostatniego_ruchu_w_obszarze()
    {
        // Kolejność nie jest tu dowolna: zegar logiczny nigdy się nie cofa, więc zapisu
        // z przeszłości nie da się zrobić po zapisie z teraźniejszości — kolejny znacznik
        // dostałby i tak czas bieżący. Scenariusz musi biec tak, jak biegnie naprawdę:
        // najpierw dawno, potem dziś.
        _zegar.Now = _zegar.Now.AddDays(-100);
        var obszar = Obszar("Twórczość", quietDays: 45);
        Zadanie("Nagrać pierwszą historię", obszar);
        _zegar.Now = _zegar.Now.AddDays(100);

        var wynik = (await _zapytania.BalanceAsync(Dzis)).Single();

        wynik.DaysSinceMove.Should().Be(100);
        wynik.IsQuiet.Should().BeTrue();
    }

    [Fact]
    public async Task Prog_ciszy_jest_wlasnoscia_obszaru_nie_stala()
    {
        // Praca i Dzieci mają progi, które praktycznie nigdy nie zadziałają, i tak ma
        // być: wartość mechanizmu leży cała w dolnej połowie tabeli (5.2).
        _zegar.Now = _zegar.Now.AddDays(-20);
        var praca = Obszar("Praca", quietDays: 14);
        var tworczosc = Obszar("Twórczość", quietDays: 45);
        Zadanie("Przegląd kodu", praca);
        Zadanie("Szkic historii", tworczosc);
        _zegar.Now = _zegar.Now.AddDays(20);

        var wynik = await _zapytania.BalanceAsync(Dzis);

        wynik.Single(b => b.Name == "Praca").IsQuiet.Should().BeTrue();
        wynik.Single(b => b.Name == "Twórczość").IsQuiet.Should().BeFalse();
    }

    [Fact]
    public async Task Tabela_idzie_od_najdluzej_milczacych()
    {
        _zegar.Now = _zegar.Now.AddDays(-5);
        var praca = Obszar("Praca");
        Obszar("Relacje");
        Zadanie("Przegląd kodu", praca);
        _zegar.Now = _zegar.Now.AddDays(5);

        (await _zapytania.BalanceAsync(Dzis)).Select(b => b.Name)
            .Should().Equal("Relacje", "Praca");
    }

    [Fact]
    public async Task Obszar_nieaktywny_znika_z_tabeli()
    {
        var obszar = Obszar("Dawny");
        obszar.SetActive(false, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.BalanceAsync(Dzis)).Should().BeEmpty();
    }

    // --- N5, N6, krok 5 ------------------------------------------------------

    [Fact]
    public async Task Po_terminie_lapie_tylko_niewykonane()
    {
        var obszar = Obszar("Finanse");
        var przeterminowane = Zadanie("Zapłacić ratę", obszar);
        przeterminowane.SetDeadline(Dzis.AddDays(-3), _hlc.Next());

        var zrobione = Zadanie("Rozliczyć PIT", obszar);
        zrobione.SetDeadline(Dzis.AddDays(-5), _hlc.Next());
        zrobione.Complete(_zegar.Now, _hlc.Next());

        var dzisiejsze = Zadanie("Przelew", obszar);
        dzisiejsze.SetDeadline(Dzis, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.OverdueAsync(Dzis)).Select(t => t.Title)
            .Should().Equal("Zapłacić ratę");
    }

    [Fact]
    public async Task Kiedys_moze_dojrzewa_gdy_minie_data_odlozenia()
    {
        var obszar = Obszar("Rozwój własny");
        var dojrzale = Zadanie("Nauczyć się hiszpańskiego", obszar);
        dojrzale.Postpone(obszar.Id, Dzis.AddDays(-1), _hlc.Next());

        var niedojrzale = Zadanie("Kupić fortepian", obszar);
        niedojrzale.Postpone(obszar.Id, Dzis.AddDays(30), _hlc.Next());

        var bezDaty = Zadanie("Kiedyś Islandia", obszar);
        bezDaty.Postpone(obszar.Id, null, _hlc.Next());
        _db.SaveChanges();

        (await _zapytania.MaturedSomedayAsync(Dzis)).Select(t => t.Title)
            .Should().Equal("Nauczyć się hiszpańskiego");
    }

    [Fact]
    public async Task Projekty_nietkniete_od_dawna_trafiaja_do_kroku_piatego()
    {
        _zegar.Now = _zegar.Now.AddDays(-30);
        var obszar = Obszar("Dom");
        Projekt("Piwnica jest uporządkowana", obszar);
        _zegar.Now = _zegar.Now.AddDays(30);
        Projekt("Zimowe opony są na aucie", obszar);

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
