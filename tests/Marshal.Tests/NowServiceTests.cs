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

        var tasks = new TaskRepository(_db);
        _teraz = new NowService(tasks, new ProjectRepository(_db), _zegar);
        _wybor = new FocusService(tasks, new UnitOfWork(_db), _zegar, _hlc);

        _obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(_obszar);
        _db.SaveChanges();
    }

    private TaskItem TaskId(
        string title,
        int? minutes = 30,
        Energy energia = Energy.Unknown,
        Guid? project = null)
    {
        var task = TaskItem.Capture(title, _zegar.Now, _hlc.Next());
        task.MakeNext(_obszar.Id, _hlc.Next());

        if (project is { } p)
        {
            task.MoveTo(_obszar.Id, p, _hlc.Next());
        }

        if (minutes is not null || energia != Energy.Unknown)
        {
            task.SetEstimate(minutes, energia, _hlc.Next());
        }

        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    private Project Projekt(string result, ProjectState stan = ProjectState.Active)
    {
        var project = new Project(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), result, _obszar.Id, _kolejnosc++);

        if (stan != ProjectState.Active)
        {
            project.SetState(stan, _hlc.Next());
        }

        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    // --- dobór kandydatów (8.1) ----------------------------------------------

    [Fact]
    public async Task Zadanie_bez_oszacowania_nigdy_nie_trafia_do_teraz()
    {
        // To nie jest surowość, tylko warunek działania: widok dobierający pod dostępne
        // minuty nie ma jak ocenić czegoś bez oszacowania.
        TaskId("nieoszacowane", minutes: null);

        (await _teraz.PickAsync(60, Energy.High)).Should().BeEmpty();
    }

    [Fact]
    public async Task Zadanie_dluzsze_niz_dostepny_czas_odpada()
    {
        TaskId("długie", minutes: 90);
        TaskId("krótkie", minutes: 20);

        (await _teraz.PickAsync(30, Energy.High)).Select(p => p.Task.Title)
            .Should().Equal("krótkie");
    }

    [Fact]
    public async Task Zadanie_wymagajace_wiecej_energii_odpada()
    {
        TaskId("ciężkie", energia: Energy.High);
        TaskId("lekkie", energia: Energy.Low);

        (await _teraz.PickAsync(60, Energy.Low)).Select(p => p.Task.Title)
            .Should().Equal("lekkie");
    }

    [Fact]
    public async Task Zadanie_o_nieznanej_energii_przechodzi_przy_kazdym_poziomie()
    {
        // Nieznana to nie to samo co niska. Brak decyzji nie może udawać decyzji.
        TaskId("nieokreślone", energia: Energy.Unknown);

        (await _teraz.PickAsync(60, Energy.Low)).Should().ContainSingle();
    }

    [Fact]
    public async Task Zadanie_odlozone_na_przyszlosc_odpada()
    {
        var task = TaskId("odłożone");
        task.Postpone(_obszar.Id, Dzis.AddDays(7), _hlc.Next());
        task.MakeNext(_obszar.Id, _hlc.Next());
        _db.SaveChanges();

        (await _teraz.PickAsync(60, Energy.High)).Should().BeEmpty();
    }

    [Fact]
    public async Task Zadanie_ze_wstrzymanego_projektu_odpada()
    {
        // Leży w bazie poprawnie, ale nie jest tym, co można teraz zrobić.
        var wstrzymany = Projekt("Kiedyś remont", ProjectState.Someday);
        TaskId("z zamrożonego", project: wstrzymany.Id);
        TaskId("samodzielne");

        (await _teraz.PickAsync(60, Energy.High)).Select(p => p.Task.Title)
            .Should().Equal("samodzielne");
    }

    // --- punktacja (8.1) -----------------------------------------------------

    [Fact]
    public async Task Termin_za_chwile_bije_wszystko()
    {
        var pilne = TaskId("z terminem");
        pilne.SetDeadline(Dzis.AddDays(1), _hlc.Next());

        var selected = TaskId("wybrane na dziś");
        selected.Focus(Dzis, _hlc.Next());
        _db.SaveChanges();

        var result = await _teraz.PickAsync(60, Energy.High);

        result[0].Task.Title.Should().Be("z terminem");
        result[0].Reason.Should().Be("termin za chwilę");
    }

    [Fact]
    public async Task Wybor_na_dzis_bije_wysoka_wage()
    {
        // Aplikacja słucha decyzji z dzisiaj, a nie etykiety sprzed pół roku.
        var wazne = TaskId("wysoka waga");
        wazne.SetPriority(Priority.High, _hlc.Next());

        var selected = TaskId("wybrane na dziś");
        selected.Focus(Dzis, _hlc.Next());
        _db.SaveChanges();

        (await _teraz.PickAsync(60, Energy.High))[0].Task.Title.Should().Be("wybrane na dziś");
    }

    [Fact]
    public async Task Jedyna_akcja_w_projekcie_dostaje_punkty_za_odblokowanie()
    {
        var project = Projekt("Opony są na aucie");
        TaskId("jedyna akcja", project: project.Id);

        var result = await _teraz.PickAsync(60, Energy.High);

        result[0].Reason.Should().Be("odblokowuje projekt");
    }

    [Fact]
    public async Task Wiek_przestaje_dawac_punkty_po_dwudziestu_dniach()
    {
        // Bez przycięcia jedno zadanie sprzed roku zdominowałoby ekran na zawsze.
        // Rok i miesiąc muszą więc dostać tyle samo punktów za wiek.
        _zegar.Now = _zegar.Now.AddYears(-1);
        TaskId("prastare");
        _zegar.Now = _zegar.Now.AddYears(1).AddDays(-25);
        TaskId("sprzed miesiąca");
        _zegar.Now = _zegar.Now.AddDays(25);

        var result = await _teraz.PickAsync(60, Energy.High);

        result.Select(p => p.Score).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Powod_podaje_wiek_prawdziwy_a_nie_przyciety()
    {
        // Przycięcie jest sposobem liczenia punktów, a nie faktem o zadaniu.
        // „Czeka 20 dni" przy zadaniu sprzed stu dni byłoby nieprawdą na ekranie.
        _zegar.Now = _zegar.Now.AddDays(-100);
        TaskId("dawne");
        _zegar.Now = _zegar.Now.AddDays(100);

        (await _teraz.PickAsync(60, Energy.High))[0].Reason.Should().Be("czeka 100 dni");
    }

    [Fact]
    public async Task Termin_w_tym_tygodniu_bije_zadanie_lezace_od_roku()
    {
        // Sedno przycięcia: leżenie długo nie może przebić faktu zewnętrznego.
        _zegar.Now = _zegar.Now.AddYears(-1);
        TaskId("prastare");
        _zegar.Now = _zegar.Now.AddYears(1);

        var zTerminem = TaskId("z terminem");
        zTerminem.SetDeadline(Dzis.AddDays(5), _hlc.Next());
        _db.SaveChanges();

        (await _teraz.PickAsync(60, Energy.High))[0].Task.Title.Should().Be("z terminem");
    }

    [Fact]
    public async Task Z_jednego_projektu_wchodzi_najwyzej_jedno_zadanie()
    {
        // Pięć kroków tego samego projektu wygląda jak praca, ale zamyka pole widzenia
        // na resztę życia.
        var project = Projekt("Duży projekt");
        for (var i = 0; i < 5; i++)
        {
            TaskId($"krok {i}", project: project.Id);
        }

        TaskId("coś zupełnie innego");

        var result = await _teraz.PickAsync(60, Energy.High);

        result.Should().HaveCount(2);
        result.Count(p => p.Task.ProjectId == project.Id).Should().Be(1);
    }

    [Fact]
    public async Task Ekran_pokazuje_najwyzej_piec_pozycji()
    {
        for (var i = 0; i < 12; i++)
        {
            TaskId($"zadanie {i}");
        }

        (await _teraz.PickAsync(60, Energy.High)).Should().HaveCount(5);
    }

    [Fact]
    public async Task Nieoszacowane_sa_liczone_zeby_dalo_sie_o_nie_poprosic()
    {
        TaskId("bez oszacowania", minutes: null);
        TaskId("oszacowane");

        (await _teraz.UnestimatedCountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(8, Energy.High)]
    [InlineData(14, Energy.Medium)]
    [InlineData(22, Energy.Low)]
    public void Podpowiedz_energii_idzie_z_pory_dnia(int hour, Energy oczekiwana)
    {
        _zegar.Now = new DateTimeOffset(2026, 9, 16, hour, 0, 0, TimeSpan.FromHours(2));

        _teraz.SuggestEnergy().Should().Be(oczekiwana);
    }

    // --- wybór na dziś (8.6, N14) --------------------------------------------

    [Fact]
    public async Task Wybor_przyjmuje_do_pieciu_zadan()
    {
        for (var i = 0; i < 5; i++)
        {
            (await _wybor.TryFocusAsync(TaskId($"zadanie {i}").Id)).Accepted.Should().BeTrue();
        }

        (await _wybor.TodayAsync()).Should().HaveCount(5);
    }

    [Fact]
    public async Task Szoste_zadanie_nie_wchodzi_i_nic_nie_zmienia()
    {
        for (var i = 0; i < 5; i++)
        {
            await _wybor.TryFocusAsync(TaskId($"zadanie {i}").Id);
        }

        var szoste = TaskId("szóste");
        var result = await _wybor.TryFocusAsync(szoste.Id);

        result.Accepted.Should().BeFalse();
        result.Current.Should().HaveCount(5);
        _db.Tasks.Single(t => t.Id == szoste.Id).FocusDate.Should().BeNull();
    }

    [Fact]
    public async Task Zdjecie_z_wyboru_robi_miejsce()
    {
        var pierwsze = TaskId("pierwsze");
        await _wybor.TryFocusAsync(pierwsze.Id);
        for (var i = 1; i < 5; i++)
        {
            await _wybor.TryFocusAsync(TaskId($"zadanie {i}").Id);
        }

        await _wybor.UnfocusAsync(pierwsze.Id);

        (await _wybor.TryFocusAsync(TaskId("szóste").Id)).Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Zdjecie_z_wyboru_nie_podbija_licznika()
    {
        // Zadanie zdjęte rano, żeby zrobić miejsce innemu, nie jest zadaniem,
        // którego nie zrobiłaś.
        var task = TaskId("pierwsze");
        await _wybor.TryFocusAsync(task.Id);
        await _wybor.UnfocusAsync(task.Id);

        _db.Tasks.Single(t => t.Id == task.Id).FocusMissCount.Should().Be(0);
    }

    /// <summary>
    /// Wzięcie z „kiedyś-może” na dziś wyjmuje zadanie z tego stanu.
    /// </summary>
    /// <remarks>
    /// Sama data wyboru zostawiała je w stanie, którego reszta aplikacji świadomie nie
    /// pokazuje: zadanie było wybrane na dziś i jednocześnie niewidoczne w „Teraz”,
    /// czyli tam, gdzie zadaje się pytanie „co teraz”. Data odroczenia znika razem
    /// ze stanem — jest obietnicą, żeby nie wracać przed czasem, a właśnie się wróciło.
    /// </remarks>
    [Fact]
    public async Task Wziecie_z_kiedys_na_dzis_czyni_zadanie_nastepna_akcja()
    {
        var task = TaskItem.Capture("Nauczyć się gotować", _zegar.Now, _hlc.Next());
        task.Postpone(_obszar.Id, Dzis.AddDays(90), _hlc.Next());
        task.SetEstimate(15, Energy.Low, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        (await _wybor.TryFocusAsync(task.Id)).Accepted.Should().BeTrue();

        var poWzieciu = _db.Tasks.Single(t => t.Id == task.Id);
        poWzieciu.State.Should().Be(TaskState.Next);
        poWzieciu.DeferUntil.Should().BeNull();
        poWzieciu.FocusDate.Should().Be(Dzis);

        (await _teraz.PickAsync(30, Energy.Medium))
            .Select(w => w.Task.Id).Should().Contain(task.Id);
    }

    [Fact]
    public async Task Ponowny_wybor_tego_samego_zadania_nie_zajmuje_drugiego_slotu()
    {
        var task = TaskId("pierwsze");

        await _wybor.TryFocusAsync(task.Id);
        await _wybor.TryFocusAsync(task.Id);

        (await _wybor.TodayAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Niewykonany_wybor_wygasa_z_licznikiem()
    {
        var task = TaskId("niezrobione");
        await _wybor.TryFocusAsync(task.Id);

        _zegar.Now = _zegar.Now.AddDays(1);
        (await _wybor.ExpireAsync()).Should().Be(1);

        var po = _db.Tasks.Single(t => t.Id == task.Id);
        po.FocusDate.Should().BeNull();
        po.FocusMissCount.Should().Be(1);
        po.State.Should().Be(TaskState.Next);
    }

    [Fact]
    public async Task Wykonane_zadanie_nie_liczy_sie_jako_pominiete()
    {
        // Bez tego N13 liczyłby zrobione zadania jako nierobione.
        var task = TaskId("zrobione");
        await _wybor.TryFocusAsync(task.Id);

        _db.Tasks.Single(t => t.Id == task.Id).Complete(_zegar.Now, _hlc.Next());
        _db.SaveChanges();

        _zegar.Now = _zegar.Now.AddDays(1);
        (await _wybor.ExpireAsync()).Should().Be(0);
        _db.Tasks.Single(t => t.Id == task.Id).FocusMissCount.Should().Be(0);
    }

    [Fact]
    public async Task Wygaszanie_puszczone_dwa_razy_liczy_raz()
    {
        var task = TaskId("niezrobione");
        await _wybor.TryFocusAsync(task.Id);
        _zegar.Now = _zegar.Now.AddDays(1);

        await _wybor.ExpireAsync();
        await _wybor.ExpireAsync();

        _db.Tasks.Single(t => t.Id == task.Id).FocusMissCount.Should().Be(1);
    }

    [Fact]
    public async Task Dzisiejszy_wybor_nie_wygasa()
    {
        var task = TaskId("na dziś");
        await _wybor.TryFocusAsync(task.Id);

        (await _wybor.ExpireAsync()).Should().Be(0);
        _db.Tasks.Single(t => t.Id == task.Id).FocusDate.Should().Be(Dzis);
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}
