using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;
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
/// Widok „Teraz" (spec 8.1) i wybór pięciu na dziś (8.6).
/// </summary>
public sealed class NowServiceTests : IDisposable
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
    private readonly NowService _teraz;
    private readonly FocusService _wybor;
    private readonly Area _obszar;
    private double _kolejnosc;

    public NowServiceTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_zegar, "biurko");

        var zadania = new TaskRepository(_db);
        _teraz = new NowService(zadania, new ProjectRepository(_db), _zegar);
        _wybor = new FocusService(zadania, new UnitOfWork(_db), _zegar, _hlc);

        _obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(_obszar);
        _db.SaveChanges();
    }

    private TaskItem Zadanie(
        string tytul,
        int? minuty = 30,
        Energy energia = Energy.Unknown,
        Guid? projekt = null)
    {
        var zadanie = TaskItem.Capture(tytul, _zegar.Now, _hlc.Next());
        zadanie.MakeNext(_obszar.Id, _hlc.Next());

        if (projekt is { } p)
        {
            zadanie.MoveTo(_obszar.Id, p, _hlc.Next());
        }

        if (minuty is not null || energia != Energy.Unknown)
        {
            zadanie.SetEstimate(minuty, energia, _hlc.Next());
        }

        _db.Tasks.Add(zadanie);
        _db.SaveChanges();
        return zadanie;
    }

    private Project Projekt(string wynik, ProjectState stan = ProjectState.Active)
    {
        var projekt = new Project(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), wynik, _obszar.Id, _kolejnosc++);

        if (stan != ProjectState.Active)
        {
            projekt.SetState(stan, _hlc.Next());
        }

        _db.Projects.Add(projekt);
        _db.SaveChanges();
        return projekt;
    }

    // --- dobór kandydatów (8.1) ----------------------------------------------

    [Fact]
    public async Task Zadanie_bez_oszacowania_nigdy_nie_trafia_do_teraz()
    {
        // To nie jest surowość, tylko warunek działania: widok dobierający pod dostępne
        // minuty nie ma jak ocenić czegoś bez oszacowania.
        Zadanie("nieoszacowane", minuty: null);

        (await _teraz.PickAsync(60, Energy.High)).Should().BeEmpty();
    }

    [Fact]
    public async Task Zadanie_dluzsze_niz_dostepny_czas_odpada()
    {
        Zadanie("długie", minuty: 90);
        Zadanie("krótkie", minuty: 20);

        (await _teraz.PickAsync(30, Energy.High)).Select(p => p.Task.Title)
            .Should().Equal("krótkie");
    }

    [Fact]
    public async Task Zadanie_wymagajace_wiecej_energii_odpada()
    {
        Zadanie("ciężkie", energia: Energy.High);
        Zadanie("lekkie", energia: Energy.Low);

        (await _teraz.PickAsync(60, Energy.Low)).Select(p => p.Task.Title)
            .Should().Equal("lekkie");
    }

    [Fact]
    public async Task Zadanie_o_nieznanej_energii_przechodzi_przy_kazdym_poziomie()
    {
        // Nieznana to nie to samo co niska. Brak decyzji nie może udawać decyzji.
        Zadanie("nieokreślone", energia: Energy.Unknown);

        (await _teraz.PickAsync(60, Energy.Low)).Should().ContainSingle();
    }

    [Fact]
    public async Task Zadanie_odlozone_na_przyszlosc_odpada()
    {
        var zadanie = Zadanie("odłożone");
        zadanie.Postpone(_obszar.Id, Dzis.AddDays(7), _hlc.Next());
        zadanie.MakeNext(_obszar.Id, _hlc.Next());
        _db.SaveChanges();

        (await _teraz.PickAsync(60, Energy.High)).Should().BeEmpty();
    }

    [Fact]
    public async Task Zadanie_ze_wstrzymanego_projektu_odpada()
    {
        // Leży w bazie poprawnie, ale nie jest tym, co można teraz zrobić.
        var wstrzymany = Projekt("Kiedyś remont", ProjectState.Someday);
        Zadanie("z zamrożonego", projekt: wstrzymany.Id);
        Zadanie("samodzielne");

        (await _teraz.PickAsync(60, Energy.High)).Select(p => p.Task.Title)
            .Should().Equal("samodzielne");
    }

    // --- punktacja (8.1) -----------------------------------------------------

    [Fact]
    public async Task Termin_za_chwile_bije_wszystko()
    {
        var pilne = Zadanie("z terminem");
        pilne.SetDeadline(Dzis.AddDays(1), _hlc.Next());

        var wybrane = Zadanie("wybrane na dziś");
        wybrane.Focus(Dzis, _hlc.Next());
        _db.SaveChanges();

        var wynik = await _teraz.PickAsync(60, Energy.High);

        wynik[0].Task.Title.Should().Be("z terminem");
        wynik[0].Reason.Should().Be("termin za chwilę");
    }

    [Fact]
    public async Task Wybor_na_dzis_bije_wysoka_wage()
    {
        // Aplikacja słucha decyzji z dzisiaj, a nie etykiety sprzed pół roku.
        var wazne = Zadanie("wysoka waga");
        wazne.SetPriority(Priority.High, _hlc.Next());

        var wybrane = Zadanie("wybrane na dziś");
        wybrane.Focus(Dzis, _hlc.Next());
        _db.SaveChanges();

        (await _teraz.PickAsync(60, Energy.High))[0].Task.Title.Should().Be("wybrane na dziś");
    }

    [Fact]
    public async Task Jedyna_akcja_w_projekcie_dostaje_punkty_za_odblokowanie()
    {
        var projekt = Projekt("Opony są na aucie");
        Zadanie("jedyna akcja", projekt: projekt.Id);

        var wynik = await _teraz.PickAsync(60, Energy.High);

        wynik[0].Reason.Should().Be("odblokowuje projekt");
    }

    [Fact]
    public async Task Wiek_przestaje_dawac_punkty_po_dwudziestu_dniach()
    {
        // Bez przycięcia jedno zadanie sprzed roku zdominowałoby ekran na zawsze.
        // Rok i miesiąc muszą więc dostać tyle samo punktów za wiek.
        _zegar.Now = _zegar.Now.AddYears(-1);
        Zadanie("prastare");
        _zegar.Now = _zegar.Now.AddYears(1).AddDays(-25);
        Zadanie("sprzed miesiąca");
        _zegar.Now = _zegar.Now.AddDays(25);

        var wynik = await _teraz.PickAsync(60, Energy.High);

        wynik.Select(p => p.Score).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Powod_podaje_wiek_prawdziwy_a_nie_przyciety()
    {
        // Przycięcie jest sposobem liczenia punktów, a nie faktem o zadaniu.
        // „Czeka 20 dni" przy zadaniu sprzed stu dni byłoby nieprawdą na ekranie.
        _zegar.Now = _zegar.Now.AddDays(-100);
        Zadanie("dawne");
        _zegar.Now = _zegar.Now.AddDays(100);

        (await _teraz.PickAsync(60, Energy.High))[0].Reason.Should().Be("czeka 100 dni");
    }

    [Fact]
    public async Task Termin_w_tym_tygodniu_bije_zadanie_lezace_od_roku()
    {
        // Sedno przycięcia: leżenie długo nie może przebić faktu zewnętrznego.
        _zegar.Now = _zegar.Now.AddYears(-1);
        Zadanie("prastare");
        _zegar.Now = _zegar.Now.AddYears(1);

        var zTerminem = Zadanie("z terminem");
        zTerminem.SetDeadline(Dzis.AddDays(5), _hlc.Next());
        _db.SaveChanges();

        (await _teraz.PickAsync(60, Energy.High))[0].Task.Title.Should().Be("z terminem");
    }

    [Fact]
    public async Task Z_jednego_projektu_wchodzi_najwyzej_jedno_zadanie()
    {
        // Pięć kroków tego samego projektu wygląda jak praca, ale zamyka pole widzenia
        // na resztę życia.
        var projekt = Projekt("Duży projekt");
        for (var i = 0; i < 5; i++)
        {
            Zadanie($"krok {i}", projekt: projekt.Id);
        }

        Zadanie("coś zupełnie innego");

        var wynik = await _teraz.PickAsync(60, Energy.High);

        wynik.Should().HaveCount(2);
        wynik.Count(p => p.Task.ProjectId == projekt.Id).Should().Be(1);
    }

    [Fact]
    public async Task Ekran_pokazuje_najwyzej_piec_pozycji()
    {
        for (var i = 0; i < 12; i++)
        {
            Zadanie($"zadanie {i}");
        }

        (await _teraz.PickAsync(60, Energy.High)).Should().HaveCount(5);
    }

    [Fact]
    public async Task Nieoszacowane_sa_liczone_zeby_dalo_sie_o_nie_poprosic()
    {
        Zadanie("bez oszacowania", minuty: null);
        Zadanie("oszacowane");

        (await _teraz.UnestimatedCountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(8, Energy.High)]
    [InlineData(14, Energy.Medium)]
    [InlineData(22, Energy.Low)]
    public void Podpowiedz_energii_idzie_z_pory_dnia(int godzina, Energy oczekiwana)
    {
        _zegar.Now = new DateTimeOffset(2026, 9, 16, godzina, 0, 0, TimeSpan.FromHours(2));

        _teraz.SuggestEnergy().Should().Be(oczekiwana);
    }

    // --- wybór na dziś (8.6, N14) --------------------------------------------

    [Fact]
    public async Task Wybor_przyjmuje_do_pieciu_zadan()
    {
        for (var i = 0; i < 5; i++)
        {
            (await _wybor.TryFocusAsync(Zadanie($"zadanie {i}").Id)).Accepted.Should().BeTrue();
        }

        (await _wybor.TodayAsync()).Should().HaveCount(5);
    }

    [Fact]
    public async Task Szoste_zadanie_nie_wchodzi_i_nic_nie_zmienia()
    {
        for (var i = 0; i < 5; i++)
        {
            await _wybor.TryFocusAsync(Zadanie($"zadanie {i}").Id);
        }

        var szoste = Zadanie("szóste");
        var wynik = await _wybor.TryFocusAsync(szoste.Id);

        wynik.Accepted.Should().BeFalse();
        wynik.Current.Should().HaveCount(5);
        _db.Tasks.Single(t => t.Id == szoste.Id).FocusDate.Should().BeNull();
    }

    [Fact]
    public async Task Zdjecie_z_wyboru_robi_miejsce()
    {
        var pierwsze = Zadanie("pierwsze");
        await _wybor.TryFocusAsync(pierwsze.Id);
        for (var i = 1; i < 5; i++)
        {
            await _wybor.TryFocusAsync(Zadanie($"zadanie {i}").Id);
        }

        await _wybor.UnfocusAsync(pierwsze.Id);

        (await _wybor.TryFocusAsync(Zadanie("szóste").Id)).Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Zdjecie_z_wyboru_nie_podbija_licznika()
    {
        // Zadanie zdjęte rano, żeby zrobić miejsce innemu, nie jest zadaniem,
        // którego nie zrobiłaś.
        var zadanie = Zadanie("pierwsze");
        await _wybor.TryFocusAsync(zadanie.Id);
        await _wybor.UnfocusAsync(zadanie.Id);

        _db.Tasks.Single(t => t.Id == zadanie.Id).FocusMissCount.Should().Be(0);
    }

    [Fact]
    public async Task Ponowny_wybor_tego_samego_zadania_nie_zajmuje_drugiego_slotu()
    {
        var zadanie = Zadanie("pierwsze");

        await _wybor.TryFocusAsync(zadanie.Id);
        await _wybor.TryFocusAsync(zadanie.Id);

        (await _wybor.TodayAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Niewykonany_wybor_wygasa_z_licznikiem()
    {
        var zadanie = Zadanie("niezrobione");
        await _wybor.TryFocusAsync(zadanie.Id);

        _zegar.Now = _zegar.Now.AddDays(1);
        (await _wybor.ExpireAsync()).Should().Be(1);

        var po = _db.Tasks.Single(t => t.Id == zadanie.Id);
        po.FocusDate.Should().BeNull();
        po.FocusMissCount.Should().Be(1);
        po.State.Should().Be(TaskState.Next);
    }

    [Fact]
    public async Task Wykonane_zadanie_nie_liczy_sie_jako_pominiete()
    {
        // Bez tego N13 liczyłby zrobione zadania jako nierobione.
        var zadanie = Zadanie("zrobione");
        await _wybor.TryFocusAsync(zadanie.Id);

        _db.Tasks.Single(t => t.Id == zadanie.Id).Complete(_zegar.Now, _hlc.Next());
        _db.SaveChanges();

        _zegar.Now = _zegar.Now.AddDays(1);
        (await _wybor.ExpireAsync()).Should().Be(0);
        _db.Tasks.Single(t => t.Id == zadanie.Id).FocusMissCount.Should().Be(0);
    }

    [Fact]
    public async Task Wygaszanie_puszczone_dwa_razy_liczy_raz()
    {
        var zadanie = Zadanie("niezrobione");
        await _wybor.TryFocusAsync(zadanie.Id);
        _zegar.Now = _zegar.Now.AddDays(1);

        await _wybor.ExpireAsync();
        await _wybor.ExpireAsync();

        _db.Tasks.Single(t => t.Id == zadanie.Id).FocusMissCount.Should().Be(1);
    }

    [Fact]
    public async Task Dzisiejszy_wybor_nie_wygasa()
    {
        var zadanie = Zadanie("na dziś");
        await _wybor.TryFocusAsync(zadanie.Id);

        (await _wybor.ExpireAsync()).Should().Be(0);
        _db.Tasks.Single(t => t.Id == zadanie.Id).FocusDate.Should().Be(Dzis);
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}
