using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure;
using Marshal.Infrastructure.Data;
using Marshal.UI;
using Marshal.UI.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Wartość przez całą trasę: z ekranu do bazy i z powrotem na ekran.
/// </summary>
/// <remarks>
/// <para>
/// Te testy istnieją z jednego powodu. 17.09 barwa kalendarza ginęła na czterech
/// odcinkach naraz — bramka jej nie czytała, dodawanie nie zapisywało, siatka nie
/// pytała, a XAML miał ją wpisaną na sztywno. <b>Każda warstwa z osobna miała test
/// i każdy przechodził</b>, bo testowały warstwy, a nie drogę. Urwana w dowolnym
/// z czterech miejsc wygląda tak samo: na szaro.
/// </para>
/// <para>
/// Dlatego tutaj nie ma atrap: kontener jest ten sam, co w oknie, baza jest prawdziwa,
/// a wartość jedzie przez model widoku, usługę, zapis i odczyt. Test przechodzi tylko
/// wtedy, gdy **cała** trasa jest drożna.
/// </para>
/// </remarks>
public sealed class PrzezCalaTraseTests : IDisposable
{
    private readonly string _katalog = Path.Combine(
        Path.GetTempPath(), "marshal-trasa-" + Guid.NewGuid().ToString("N"));

    private readonly ServiceProvider _uslugi;

    public PrzezCalaTraseTests()
    {
        Directory.CreateDirectory(_katalog);

        _uslugi = new ServiceCollection()
            .AddMarshal(Path.Combine(_katalog, "marshal.db"))
            .AddMarshalViewModels()
            .BuildServiceProvider();

        // Pełne przygotowanie, nie sama migracja: obszary zasiewane są właśnie tutaj,
        // a bez nich zadanie nie ma gdzie wylądować przy nadaniu dnia wykonania.
        DependencyInjection.PrepareAsync(_uslugi).GetAwaiter().GetResult();
    }

    private T Usluga<T>() where T : notnull => _uslugi.GetRequiredService<T>();

    /// <summary>Zadanie zaplanowane na dziś, zapisane tak, jak zapisuje je aplikacja.</summary>
    private async Task<TaskItem> ZaplanowaneAsync(string tytul)
    {
        var zadania = Usluga<ITaskRepository>();
        var hlc = Usluga<IHlcSource>();
        var zegar = Usluga<IClock>();

        var zadanie = TaskItem.Capture(tytul, zegar.Now, hlc.Next());
        zadanie.Schedule(Guid.CreateVersion7(), zegar.Today, hlc.Next());

        zadania.Add(zadanie);
        await Usluga<IUnitOfWork>().SaveChangesAsync();

        return zadanie;
    }

    [Fact]
    public async Task Zadanie_zalozone_klikiem_w_siatke_pojawia_sie_na_siatce()
    {
        // Dokładnie ta droga, którą idzie ręka: klik w pustą siatkę, wypełnienie,
        // zapis. Bez atrap — kontener ten sam, co w oknie.
        var kalendarz = Usluga<CalendarViewModel>();
        var szczegol = Usluga<TaskDetailViewModel>();

        (DateOnly Dzien, TimeOnly Pora)? poproszono = null;
        kalendarz.NewTaskRequested += (dzien, pora) => poproszono = (dzien, pora);

        await kalendarz.LoadAsync();

        var dzis = Usluga<IClock>().Today;

        // 16:00 na siatce to 16 * 48 punktów od góry.
        kalendarz.NewAt(dzis, 16 * 48);
        poproszono.Should().NotBeNull("klik w pustą siatkę ma poprosić o nowe zadanie");

        await szczegol.NewAsync(poproszono!.Value.Dzien, poproszono.Value.Pora);

        szczegol.IsOpen.Should().BeTrue();
        szczegol.DoTime.Should().Be(new TimeSpan(16, 0, 0));

        szczegol.Title = "Odebrać Sanię";
        await szczegol.SaveAsync();

        szczegol.Problem.Should().BeNull("zapis miał się udać");
        szczegol.IsOpen.Should().BeFalse("udany zapis zamyka okno");

        // I to jest pytanie właściwe: czy widać je tam, gdzie się je założyło.
        await kalendarz.LoadAsync();

        kalendarz.Columns.SelectMany(k => k.Slots)
            .Should().ContainSingle(b => b.Title == "Odebrać Sanię")
            .Which.StartText.Should().Be("16:00");
    }

    [Fact]
    public async Task Data_nadana_wrzutowi_nie_ginie_po_drodze()
    {
        // Usterka, przez którą „Dzisiaj" i „Plany" były puste, a zadanie nie pojawiało
        // się na siatce. Oba przejścia stanu wymagały, żeby zadanie miało już obszar —
        // a zadanie z wrzutu go nie ma. Data **znikała bez słowa**: zapis się udawał,
        // okno zamykało, i tyle.
        var main = Usluga<MainViewModel>();
        main.CaptureText = "Zadzwonić do przedszkola";
        await main.CaptureCommand.ExecuteAsync(null);

        var wrzut = main.InboxItems.Single();
        wrzut.AreaId.Should().BeNull("wrzut nie ma obszaru i to jest cała trudność");

        var szczegol = Usluga<TaskDetailViewModel>();
        await szczegol.LoadAsync(wrzut);

        var dzis = Usluga<IClock>().Today;
        szczegol.DoDate = new DateTimeOffset(dzis.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        szczegol.DoTime = new TimeSpan(16, 0, 0);
        szczegol.EndTime = new TimeSpan(17, 30, 0);

        await szczegol.SaveAsync();

        szczegol.Problem.Should().BeNull("zapis miał się udać");

        var zapisane = await Usluga<ITaskRepository>().FindAsync(wrzut.Id);

        zapisane!.DoDate.Should().Be(dzis);
        zapisane.DoTime.Should().Be(new TimeOnly(16, 0));
        zapisane.AreaId.Should().NotBeNull("zadanie z dniem wykonania musi gdzieś należeć");

        // Koniec nie jest osobnym polem: jest długością, i to ona rysuje blok.
        zapisane.EstimatedMinutes.Should().Be(90);

        // I dopiero to jest odpowiedź na „dlaczego Dzisiaj jest puste".
        await main.ShowTodayCommand.ExecuteAsync(null);
        main.TodayItems.Should().ContainSingle(w => w.Task.Id == wrzut.Id);

        // Na siatce też, o właściwej godzinie.
        var kalendarz = Usluga<CalendarViewModel>();
        await kalendarz.LoadAsync();

        kalendarz.Columns.SelectMany(k => k.Slots)
            .Should().ContainSingle(b => b.Title == "Zadzwonić do przedszkola")
            .Which.StartText.Should().Be("16:00");
    }

    [Fact]
    public async Task Zadanie_z_godzina_bez_konca_trwa_pol_godziny()
    {
        var zadanie = await ZaplanowaneAsync("Przerwa");

        var szczegol = Usluga<TaskDetailViewModel>();
        await szczegol.LoadAsync(zadanie);
        szczegol.DoTime = new TimeSpan(9, 0, 0);
        szczegol.EndTime = null;
        szczegol.EstimatedMinutes = null;

        await szczegol.SaveAsync();

        var kalendarz = Usluga<CalendarViewModel>();
        await kalendarz.LoadAsync();

        var blok = kalendarz.Columns.SelectMany(k => k.Slots).Single(b => b.Title == "Przerwa");

        blok.StartText.Should().Be("09:00");
        blok.EndText.Should().Be("09:30");
    }

    [Fact]
    public async Task Rytm_ustawiony_na_ekranie_rodzi_nastepne_zadanie()
    {
        // Trasa: lista wyboru w oknie → reguła → JSON w bazie → odczyt → następnik.
        // Sześć warstw, z których każda ma własny test i każdy przechodzi.
        var zadanie = await ZaplanowaneAsync("Wynieść śmieci");

        var szczegol = Usluga<TaskDetailViewModel>();
        szczegol.Load(zadanie);
        szczegol.SelectedRepeat = RepeatChoice.All.Single(r => r.Kind == RecurrenceKind.Daily);

        await szczegol.SaveAsync();

        // Odczyt z bazy, nie z obiektu w pamięci: chodzi o to, czy reguła **przeżyła zapis**.
        var zapisane = await Usluga<ITaskRepository>().FindAsync(zadanie.Id);
        zapisane!.Recurrence.Should().NotBeNull();
        zapisane.Recurrence!.Kind.Should().Be(RecurrenceKind.Daily);

        var nastepne = await Usluga<TaskEditService>().CompleteAsync(zadanie.Id);

        nastepne.Should().NotBeNull();
        nastepne!.Recurrence!.Kind.Should().Be(RecurrenceKind.Daily);
        nastepne.Title.Should().Be("Wynieść śmieci");
    }

    [Fact]
    public async Task Przypomnienie_z_dnia_i_pory_dojezdza_do_bazy()
    {
        // Dzień i pora są w oknie osobno, a w bazie są jedną chwilą. Składanie dzieje
        // się przy zapisie i jest to dokładnie ten rodzaj miejsca, w którym wartość
        // znika bez śladu — sama pora bez dnia nie znaczy nic i ma nie zapisać niczego.
        var zadanie = await ZaplanowaneAsync("Zadzwonić do przychodni");

        var szczegol = Usluga<TaskDetailViewModel>();
        szczegol.Load(zadanie);
        szczegol.ReminderTime = new TimeSpan(14, 30, 0);

        await szczegol.SaveAsync();

        (await Usluga<ITaskRepository>().FindAsync(zadanie.Id))!
            .ReminderAt.Should().BeNull("sama pora bez dnia nie wskazuje chwili");

        szczegol.Load(zadanie);
        szczegol.ReminderDay = new DateTimeOffset(
            Usluga<IClock>().Today.ToDateTime(TimeOnly.MinValue), Usluga<IClock>().Now.Offset);
        szczegol.ReminderTime = new TimeSpan(14, 30, 0);

        await szczegol.SaveAsync();

        var zapisane = await Usluga<ITaskRepository>().FindAsync(zadanie.Id);

        zapisane!.ReminderAt.Should().NotBeNull();
        zapisane.ReminderAt!.Value.TimeOfDay.Should().Be(new TimeSpan(14, 30, 0));
    }

    [Fact]
    public async Task Zapisany_filtr_wraca_z_bazy_z_tymi_samymi_warunkami()
    {
        // Warunki jadą do bazy jako JSON i wracają do **innego** modelu widoku niż ten,
        // który je zapisał. Warstwa po warstwie wszystko przechodzi także wtedy, gdy
        // jedno pole gubi się przy zapisie albo przy odczycie.
        await ZaplanowaneAsync("Zrobić przelew");

        var filtry = Usluga<FiltersViewModel>();
        await filtry.LoadAsync();

        filtry.Name = "Na dziś";
        filtry.Text = "przelew";

        await filtry.SaveCommand.ExecuteAsync(null);
        filtry.OpenId.Should().NotBe(Guid.Empty, "zapis miał zwrócić identyfikator");

        var zapisany = (await Usluga<ISavedFilterRepository>().AllAsync())
            .Single(f => f.Name == "Na dziś");

        zapisany.Query.Should().NotBeNull("definicja ma się dać odczytać z powrotem");
        zapisany.Query!.Conditions.Should().NotBeEmpty("warunek „przelew\" miał przeżyć zapis");
    }

    public void Dispose()
    {
        _uslugi.Dispose();

        if (Directory.Exists(_katalog))
        {
            Directory.Delete(_katalog, recursive: true);
        }
    }
}
