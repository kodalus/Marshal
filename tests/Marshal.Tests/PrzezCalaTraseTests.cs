using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure;
using Marshal.Infrastructure.Data;
using Marshal.UI;
using Marshal.Domain.Areas;
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

    /// <summary>
    /// Cała droga z „Kiedyś" do „Teraz": wzięcie na dziś i dopisanie oszacowania.
    /// </summary>
    /// <remarks>
    /// Trasa rwała się w dwóch miejscach naraz, a każde z osobna miało przechodzący
    /// test. Wzięcie na dziś przeliczało samą piątkę, więc lista „Kiedyś" pokazywała
    /// zadanie dalej w starym miejscu — wyglądało to jak kliknięcie bez skutku.
    /// A „Teraz" pomija zadania bez oszacowania, więc nawet wzięte na dziś nie miało
    /// jak się tam pojawić, bo oszacowanie dawało się wpisać wyłącznie w szczegółach.
    /// </remarks>
    [Fact]
    public async Task Zadanie_z_kiedys_da_sie_wziac_na_dzis_i_zobaczyc_w_teraz()
    {
        var main = Usluga<MainViewModel>();
        var obszar = (await Usluga<IAreaRepository>().ActiveAsync())[0];

        var zadania = Usluga<ITaskRepository>();
        var hlc = Usluga<IHlcSource>();
        var zegar = Usluga<IClock>();

        var zadanie = TaskItem.Capture("Nauczyć się szyć", zegar.Now, hlc.Next());
        zadanie.Postpone(obszar.Id, zegar.Today.AddDays(90), hlc.Next());
        zadania.Add(zadanie);
        await Usluga<IUnitOfWork>().SaveChangesAsync();

        await main.ShowSomedayCommand.ExecuteAsync(null);
        main.SomedayItems.Should().ContainSingle(t => t.Id == zadanie.Id);

        await main.FocusTaskAsync(zadanie);

        main.Notice.Should().BeEmpty("piątka jest pusta, więc nie ma czego odmawiać");
        main.SomedayItems.Should().NotContain(
            t => t.Id == zadanie.Id, "wzięte na dziś przestaje być „kiedyś”");
        main.FocusItems.Should().ContainSingle(t => t.Id == zadanie.Id);

        // Bez oszacowania „Teraz" go nie zobaczy — i to jest poprawne, bo ten ekran
        // pyta „ile mam czasu". Dopisanie idzie tą samą drogą co z menu podręcznego.
        var teraz = main.Now;
        await teraz.LoadAsync();
        teraz.Picks.Should().NotContain(w => w.Task.Id == zadanie.Id);

        await main.SetEstimateAsync(zadanie, 15);

        // Odczyt z bazy między zmianami: obie idą przez to samo wywołanie usługi,
        // które ustawia oszacowanie i siłę naraz, więc druga musi widzieć pierwszą.
        var swieze = await zadania.FindAsync(zadanie.Id);
        await main.SetEnergyAsync(swieze!, Energy.Low);

        (await zadania.FindAsync(zadanie.Id))!.EstimatedMinutes.Should().Be(
            15, "dopisanie siły nie ma kasować oszacowania");

        await teraz.LoadAsync();
        teraz.Picks.Should().Contain(w => w.Task.Id == zadanie.Id);
    }

    /// <summary>
    /// „Kiedyś" da się pokazać w „Teraz" i na liście kandydatów — na wyraźne życzenie.
    /// </summary>
    /// <remarks>
    /// Zadanie z dopisaną długością i siłą jest już opisane tak, jak opisuje się rzeczy
    /// do zrobienia, a mimo to nie dawało się go wybrać nigdzie poza menu podręcznym
    /// na ekranie „Kiedyś". Granica zostaje — przełącznik jest wyłączony domyślnie
    /// i zeruje się przy każdym wejściu — ale da się ją przekroczyć.
    /// </remarks>
    [Fact]
    public async Task Kiedys_z_dlugoscia_i_sila_da_sie_pokazac_w_teraz_i_w_kandydatach()
    {
        var main = Usluga<MainViewModel>();
        var obszar = (await Usluga<IAreaRepository>().ActiveAsync())[0];

        var zadania = Usluga<ITaskRepository>();
        var hlc = Usluga<IHlcSource>();
        var zegar = Usluga<IClock>();

        var zadanie = TaskItem.Capture("test na 15 minut i resztkę energii", zegar.Now, hlc.Next());
        zadanie.Postpone(obszar.Id, null, hlc.Next());
        zadanie.SetEstimate(15, Energy.Low, hlc.Next());
        zadania.Add(zadanie);
        await Usluga<IUnitOfWork>().SaveChangesAsync();

        await main.Now.LoadAsync();
        main.Now.AlsoSomeday.Should().BeFalse("wejście na ekran nie otwiera go na wszystko");
        main.Now.Picks.Should().NotContain(w => w.Task.Id == zadanie.Id);

        // Sam dobór sprawdzany wprost w usłudze, nie przez przełącznik w oknie:
        // przeliczenie po zmianie pola idzie bez czekania i test nie ma czego dopilnować,
        // a pytanie dotyczy tego, kogo dobieranie bierze pod uwagę.
        var teraz = Usluga<NowService>();
        (await teraz.PickAsync(30, Energy.Medium))
            .Should().NotContain(w => w.Task.Id == zadanie.Id);
        (await teraz.PickAsync(30, Energy.Medium, includeSomeday: true))
            .Should().Contain(w => w.Task.Id == zadanie.Id);

        await main.ShowTodayCommand.ExecuteAsync(null);
        main.FocusCandidates.Should().NotContain(w => w.Task.Id == zadanie.Id);

        main.AlsoSomeday = true;
        await main.ShowTodayCommand.ExecuteAsync(null);
        main.FocusCandidates.Should().Contain(w => w.Task.Id == zadanie.Id);
    }

    /// <summary>
    /// Obszar da się założyć, przemianować i usunąć — ale tylko pusty.
    /// </summary>
    /// <remarks>
    /// Podział odpowiedzialności jest rzeczą osobistą i zmienia się w życiu. Zasiana
    /// dziesiątka jest punktem wyjścia, a nie zestawem, w który trzeba się wcisnąć.
    /// Usunięcie obszaru z zawartością nie ma dobrej odpowiedzi: każde zadanie musi
    /// należeć do obszaru (N11), więc skasowanie zostawiłoby sieroty.
    /// </remarks>
    [Fact]
    public async Task Obszar_da_sie_zalozyc_przemianowac_i_usunac_gdy_pusty()
    {
        var main = Usluga<MainViewModel>();

        main.NewAreaName = "Rodzina";
        await main.AddAreaCommand.ExecuteAsync(null);

        main.NewAreaName.Should().BeEmpty("pole ma się opróżnić po dodaniu");

        var dodany = main.ProjectRows.Should()
            .ContainSingle(w => w.Label == "Rodzina" && w.IsArea).Which;

        // Liczby równowagi stoją przy obszarze, a nie w osobnej tabeli na innym ekranie.
        dodany.HasBalance.Should().BeTrue();

        await main.RenameRowAsync(dodany, "Rodzina i dom");
        var poNazwie = main.ProjectRows.Should()
            .ContainSingle(w => w.Id == dodany.Id).Which;
        poNazwie.Label.Should().Be("Rodzina i dom");

        // Projekt zakłada się pod obszarem, z tego samego menu co reszta.
        await main.AddProjectAsync(poNazwie, "Kuchnia wyremontowana");
        var projekt = main.ProjectRows.Should()
            .ContainSingle(w => w.Label == "Kuchnia wyremontowana").Which;
        projekt.IsArea.Should().BeFalse();
        projekt.AreaId.Should().Be(poNazwie.Id, "podprojekt dziedziczy obszar rodzica");

        // Obszar z projektem w środku ma odmówić usunięcia i powiedzieć dlaczego.
        await main.DeleteRowAsync(poNazwie);
        main.Notice.Should().Contain("projekty");
        main.ProjectRows.Should().Contain(w => w.Id == poNazwie.Id);

        // Zadanie w projekcie blokuje usunięcie projektu z tego samego powodu.
        var zadania = Usluga<ITaskRepository>();
        var hlc = Usluga<IHlcSource>();
        var zegar = Usluga<IClock>();

        var zadanie = TaskItem.Capture("Zakupy", zegar.Now, hlc.Next());
        zadanie.MakeNext(poNazwie.Id, hlc.Next());
        zadanie.MoveTo(poNazwie.Id, projekt.Id, hlc.Next());
        zadania.Add(zadanie);
        await Usluga<IUnitOfWork>().SaveChangesAsync();

        await main.ShowProjectsCommand.ExecuteAsync(null);
        await main.DeleteRowAsync(main.ProjectRows.Single(w => w.Id == projekt.Id));
        main.Notice.Should().Contain("zadania");

        // Po opróżnieniu schodzi wszystko: najpierw projekt, potem obszar.
        await main.TrashTaskAsync(zadanie);
        await main.ShowProjectsCommand.ExecuteAsync(null);

        await main.DeleteRowAsync(main.ProjectRows.Single(w => w.Id == projekt.Id));
        main.Notice.Should().BeEmpty();

        await main.DeleteRowAsync(main.ProjectRows.Single(w => w.Id == poNazwie.Id));
        main.Notice.Should().BeEmpty();
        main.ProjectRows.Should().NotContain(w => w.Id == poNazwie.Id);
    }

    /// <summary>
    /// „Robię to dzisiaj” ze skrzynki: wrzut z oszacowaniem ląduje w piątce i w „Teraz".
    /// </summary>
    /// <remarks>
    /// Z przetwarzania nie dawało się wziąć czegoś na dziś w ogóle. „Zaplanuj" nadaje
    /// dzień, czyli stawia zadanie na siatce kalendarza — a to jest inna decyzja niż
    /// „robię to dzisiaj, nie wiem o której". Przy wrzucie, który się właśnie
    /// oszacowało, ta druga jest częstsza i była jedyną, której brakowało.
    /// </remarks>
    [Fact]
    public async Task Wrzut_da_sie_wziac_na_dzis_wprost_z_przetwarzania()
    {
        var main = Usluga<MainViewModel>();
        main.CaptureText = "Zrobić zakupy";
        await main.CaptureCommand.ExecuteAsync(null);

        var clarify = main.Clarify;
        await clarify.LoadAsync();

        clarify.Current.Should().NotBeNull();
        clarify.Areas.Should().NotBeEmpty("miejsce trzeba z czegoś wybrać");
        clarify.SelectedArea = clarify.Areas.First(m => m.ProjectId is null);
        clarify.EstimatedMinutes = 30;
        clarify.SelectedEnergy = EnergyLevelChoice.All.First(e => e.Value == Energy.Medium);

        await clarify.TodayCommand.ExecuteAsync(null);

        clarify.Problem.Should().BeNull("piątka jest pusta, nie ma czego odmawiać");

        var zadanie = (await Usluga<ITaskRepository>().ByStateAsync(TaskState.Next))
            .Should().ContainSingle(t => t.Title == "Zrobić zakupy").Which;

        zadanie.EstimatedMinutes.Should().Be(30);
        zadanie.Energy.Should().Be(Energy.Medium);
        zadanie.FocusDate.Should().Be(Usluga<IClock>().Today, "„dzisiaj” znaczy piątkę na dziś");

        await main.ShowTodayCommand.ExecuteAsync(null);
        main.FocusItems.Should().ContainSingle(t => t.Id == zadanie.Id);

        (await Usluga<NowService>().PickAsync(30, Energy.Medium))
            .Should().Contain(w => w.Task.Id == zadanie.Id);
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
    public async Task Oszacowanie_z_przetwarzania_dociera_do_ekranu_teraz()
    {
        // Sedno: ekran „Teraz" dobiera pod dostępne minuty i poziom sił, więc zadanie
        // bez oszacowania nie trafia tam nigdy. Dotąd dało się to wpisać dopiero
        // w szczegółach — po przetworzeniu, z listy, drugim otwarciem.
        var main = Usluga<MainViewModel>();

        main.CaptureText = "Zadzwonić do przychodni";
        await main.CaptureCommand.ExecuteAsync(null);

        var przetwarzanie = Usluga<ClarifyViewModel>();
        await przetwarzanie.LoadAsync();

        przetwarzanie.Current.Should().NotBeNull();
        przetwarzanie.SelectedArea = przetwarzanie.Areas.First();
        przetwarzanie.EstimatedMinutes = 15;
        przetwarzanie.SelectedEnergy = EnergyLevelChoice.All.Single(e => e.Value == Energy.Low);

        await przetwarzanie.MakeNextCommand.ExecuteAsync(null);

        var teraz = main.Now;
        await teraz.LoadAsync();

        teraz.SelectedMinutes = MinutesChoice.All.First(m => m.Minutes >= 15);
        teraz.SelectedEnergy = EnergyChoice.All.Single(e => e.Value == Energy.Low);
        await teraz.RefreshCommand.ExecuteAsync(null);

        teraz.Picks.Should().Contain(w => w.Task.Title == "Zadzwonić do przychodni");
    }

    [Fact]
    public async Task Czynnosci_menu_podrecznego_robia_to_co_obiecuja()
    {
        // W menu mają być **tylko rzeczy, które działają**: pozycja, która nic nie robi,
        // uczy nieufności do całego menu, a nieufne menu przestaje być skrótem.
        var main = Usluga<MainViewModel>();
        var dzis = Usluga<IClock>().Today;

        var jutro = await ZaplanowaneAsync("Przełożyć");
        await main.PostponeTaskCommand.ExecuteAsync(jutro);

        (await Usluga<ITaskRepository>().FindAsync(jutro.Id))!
            .DoDate.Should().Be(dzis.AddDays(1));

        var kosz = await ZaplanowaneAsync("Wyrzucić");
        await main.TrashTaskCommand.ExecuteAsync(kosz);

        (await Usluga<ITaskRepository>().FindAsync(kosz.Id))!
            .State.Should().Be(TaskState.Trashed);

        var piatka = await ZaplanowaneAsync("Na dziś");
        await main.FocusTaskCommand.ExecuteAsync(piatka);

        main.FocusItems.Should().Contain(z => z.Id == piatka.Id);

        var szczegol = await ZaplanowaneAsync("Otworzyć");
        await main.OpenTaskCommand.ExecuteAsync(szczegol);

        Usluga<TaskDetailViewModel>().IsOpen.Should().BeTrue();
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
    public async Task Wyprzedzenia_z_okna_dojezdzaja_do_bazy()
    {
        // Kwadraciki w oknie, lista minut w bazie, chwile w przypomnieniach — trzy
        // różne kształty tej samej rzeczy. Test idzie przez wszystkie trzy, bo każde
        // przejście jest miejscem, w którym wyprzedzenie może cicho zniknąć.
        var zadanie = await ZaplanowaneAsync("Wizyta u lekarza");

        var szczegol = Usluga<TaskDetailViewModel>();
        await szczegol.LoadAsync(zadanie);

        szczegol.HasTime.Should().BeFalse("zadanie jeszcze nie ma godziny");

        szczegol.DoTime = new TimeSpan(14, 0, 0);

        szczegol.HasTime.Should().BeTrue();
        szczegol.Leads.Single(w => w.Minutes == 0).IsChecked
            .Should().BeTrue("wpisana godzina sama włącza przypomnienie o czasie");

        szczegol.Leads.Single(w => w.Minutes == 30).IsChecked = true;

        // Dowolnie: dwie godziny. Ma wskoczyć na listę na swoje miejsce w kolejności
        // czasu i od razu zostać zaznaczone.
        szczegol.CustomLead = 2;
        szczegol.SelectedLeadUnit = LeadUnitChoice.All.Single(j => j.Label == "godzin");
        szczegol.AddLeadCommand.Execute(null);

        var dopisane = szczegol.Leads.Single(w => w.Minutes == 120);
        dopisane.IsChecked.Should().BeTrue();
        dopisane.Label.Should().Be("2 godz. wcześniej");
        szczegol.Leads.Select(w => w.Minutes).Should().BeInAscendingOrder();

        await szczegol.SaveAsync();

        var zapisane = await Usluga<ITaskRepository>().FindAsync(zadanie.Id);
        zapisane!.ReminderLeads.Should().Equal(0, 30, 120);

        // Droga powrotna: okno otwarte drugi raz ma pokazać to samo.
        szczegol.Load(zapisane);
        szczegol.Leads.Where(w => w.IsChecked).Select(w => w.Minutes)
            .Should().Equal(new[] { 0, 30, 120 }, "wczytanie ma pokazać to samo, co się zapisało");

        // Zabrana godzina zabiera to, od czego wyprzedzenia się liczyły — więc i je.
        szczegol.DoTime = null;

        szczegol.Leads.Should().NotContain(
            w => w.IsChecked, "okno ma pokazywać to, co się zapisze");

        await szczegol.SaveAsync();

        (await Usluga<ITaskRepository>().FindAsync(zadanie.Id))!
            .ReminderLeads.Should().BeEmpty("bez godziny nie ma od czego liczyć");
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
        zapisany.Query!.Conditions.Should().NotBeEmpty("warunek „przelew” miał przeżyć zapis");
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
