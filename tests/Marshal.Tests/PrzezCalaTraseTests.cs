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
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "marshal-trasa-" + Guid.NewGuid().ToString("N"));

    private readonly ServiceProvider _services;

    public PrzezCalaTraseTests()
    {
        Directory.CreateDirectory(_folder);

        _services = new ServiceCollection()
            .AddMarshal(Path.Combine(_folder, "marshal.db"))
            .AddMarshalViewModels()
            .BuildServiceProvider();

        // Pełne przygotowanie, nie sama migracja: obszary zasiewane są właśnie tutaj,
        // a bez nich zadanie nie ma gdzie wylądować przy nadaniu dnia wykonania.
        DependencyInjection.PrepareAsync(_services).GetAwaiter().GetResult();
    }

    private T NewService<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>Zadanie zaplanowane na dziś, zapisane tak, jak zapisuje je aplikacja.</summary>
    private async Task<TaskItem> ZaplanowaneAsync(string title)
    {
        var tasks = NewService<ITaskRepository>();
        var hlc = NewService<IHlcSource>();
        var clock = NewService<IClock>();

        var task = TaskItem.Capture(title, clock.Now, hlc.Next());
        task.Schedule(Guid.CreateVersion7(), clock.Today, hlc.Next());

        tasks.Add(task);
        await NewService<IUnitOfWork>().SaveChangesAsync();

        return task;
    }

    [Fact]
    public async Task Zadanie_zalozone_klikiem_w_siatke_pojawia_sie_na_siatce()
    {
        // Dokładnie ta droga, którą idzie ręka: klik w pustą siatkę, wypełnienie,
        // zapis. Bez atrap — kontener ten sam, co w oknie.
        var calendarId = NewService<CalendarViewModel>();
        var detail = NewService<TaskDetailViewModel>();

        (DateOnly Day, TimeOnly Time)? poproszono = null;
        calendarId.NewTaskRequested += (day, time) => poproszono = (day, time);

        await calendarId.LoadAsync();

        var today = NewService<IClock>().Today;

        // 16:00 na siatce to 16 * 48 punktów od góry.
        calendarId.NewAt(today, 16 * 48);
        poproszono.Should().NotBeNull("klik w pustą siatkę ma poprosić o nowe zadanie");

        await detail.NewAsync(poproszono!.Value.Day, poproszono.Value.Time);

        detail.IsOpen.Should().BeTrue();
        detail.DoTime.Should().Be(new TimeSpan(16, 0, 0));

        detail.Title = "Odebrać Sanię";
        await detail.SaveAsync();

        detail.Problem.Should().BeNull("zapis miał się udać");
        detail.IsOpen.Should().BeFalse("udany zapis zamyka okno");

        // I to jest pytanie właściwe: czy widać je tam, gdzie się je założyło.
        await calendarId.LoadAsync();

        calendarId.Columns.SelectMany(k => k.Slots)
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
        var main = NewService<MainViewModel>();
        var area = (await NewService<IAreaRepository>().ActiveAsync())[0];

        var tasks = NewService<ITaskRepository>();
        var hlc = NewService<IHlcSource>();
        var clock = NewService<IClock>();

        var task = TaskItem.Capture("Nauczyć się szyć", clock.Now, hlc.Next());
        task.Postpone(area.Id, clock.Today.AddDays(90), hlc.Next());
        tasks.Add(task);
        await NewService<IUnitOfWork>().SaveChangesAsync();

        await main.ShowSomedayCommand.ExecuteAsync(null);
        main.SomedayItems.Should().ContainSingle(t => t.Id == task.Id);

        await main.FocusTaskAsync(task);

        main.Notice.Should().BeEmpty("piątka jest pusta, więc nie ma czego odmawiać");
        main.SomedayItems.Should().NotContain(
            t => t.Id == task.Id, "wzięte na dziś przestaje być „kiedyś”");
        main.FocusItems.Should().ContainSingle(t => t.Id == task.Id);

        // Bez oszacowania „Teraz" go nie zobaczy — i to jest poprawne, bo ten ekran
        // pyta „ile mam czasu". Dopisanie idzie tą samą drogą co z menu podręcznego.
        var now = main.Now;
        await now.LoadAsync();
        now.Picks.Should().NotContain(w => w.Task.Id == task.Id);

        await main.SetEstimateAsync(task, 15);

        // Odczyt z bazy między zmianami: obie idą przez to samo wywołanie usługi,
        // które ustawia oszacowanie i siłę naraz, więc druga musi widzieć pierwszą.
        var swieze = await tasks.FindAsync(task.Id);
        await main.SetEnergyAsync(swieze!, Energy.Low);

        (await tasks.FindAsync(task.Id))!.EstimatedMinutes.Should().Be(
            15, "dopisanie siły nie ma kasować oszacowania");

        await now.LoadAsync();
        now.Picks.Should().Contain(w => w.Task.Id == task.Id);
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
        var main = NewService<MainViewModel>();
        var area = (await NewService<IAreaRepository>().ActiveAsync())[0];

        var tasks = NewService<ITaskRepository>();
        var hlc = NewService<IHlcSource>();
        var clock = NewService<IClock>();

        var task = TaskItem.Capture("test na 15 minut i resztkę energii", clock.Now, hlc.Next());
        task.Postpone(area.Id, null, hlc.Next());
        task.SetEstimate(15, Energy.Low, hlc.Next());
        tasks.Add(task);
        await NewService<IUnitOfWork>().SaveChangesAsync();

        await main.Now.LoadAsync();
        main.Now.AlsoSomeday.Should().BeFalse("wejście na ekran nie otwiera go na wszystko");
        main.Now.Picks.Should().NotContain(w => w.Task.Id == task.Id);

        // Sam dobór sprawdzany wprost w usłudze, nie przez przełącznik w oknie:
        // przeliczenie po zmianie pola idzie bez czekania i test nie ma czego dopilnować,
        // a pytanie dotyczy tego, kogo dobieranie bierze pod uwagę.
        var now = NewService<NowService>();
        (await now.PickAsync(30, Energy.Medium))
            .Should().NotContain(w => w.Task.Id == task.Id);
        (await now.PickAsync(30, Energy.Medium, includeSomeday: true))
            .Should().Contain(w => w.Task.Id == task.Id);

        await main.ShowTodayCommand.ExecuteAsync(null);
        main.FocusCandidates.Should().NotContain(w => w.Task.Id == task.Id);

        main.AlsoSomeday = true;
        await main.ShowTodayCommand.ExecuteAsync(null);
        main.FocusCandidates.Should().Contain(w => w.Task.Id == task.Id);
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
        var main = NewService<MainViewModel>();

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
        var project = main.ProjectRows.Should()
            .ContainSingle(w => w.Label == "Kuchnia wyremontowana").Which;
        project.IsArea.Should().BeFalse();
        project.AreaId.Should().Be(poNazwie.Id, "podprojekt dziedziczy obszar rodzica");

        // Obszar z projektem w środku ma odmówić usunięcia i powiedzieć dlaczego.
        await main.DeleteRowAsync(poNazwie);
        main.Notice.Should().Contain("projekty");
        main.ProjectRows.Should().Contain(w => w.Id == poNazwie.Id);

        // Zadanie w projekcie blokuje usunięcie projektu z tego samego powodu.
        var tasks = NewService<ITaskRepository>();
        var hlc = NewService<IHlcSource>();
        var clock = NewService<IClock>();

        var task = TaskItem.Capture("Zakupy", clock.Now, hlc.Next());
        task.MakeNext(poNazwie.Id, hlc.Next());
        task.MoveTo(poNazwie.Id, project.Id, hlc.Next());
        tasks.Add(task);
        await NewService<IUnitOfWork>().SaveChangesAsync();

        await main.ShowProjectsCommand.ExecuteAsync(null);
        await main.DeleteRowAsync(main.ProjectRows.Single(w => w.Id == project.Id));
        main.Notice.Should().Contain("zadania");

        // Po opróżnieniu schodzi wszystko: najpierw projekt, potem obszar.
        await main.TrashTaskAsync(task);
        await main.ShowProjectsCommand.ExecuteAsync(null);

        await main.DeleteRowAsync(main.ProjectRows.Single(w => w.Id == project.Id));
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
        var main = NewService<MainViewModel>();
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

        var task = (await NewService<ITaskRepository>().ByStateAsync(TaskState.Next))
            .Should().ContainSingle(t => t.Title == "Zrobić zakupy").Which;

        task.EstimatedMinutes.Should().Be(30);
        task.Energy.Should().Be(Energy.Medium);
        task.FocusDate.Should().Be(NewService<IClock>().Today, "„dzisiaj” znaczy piątkę na dziś");

        await main.ShowTodayCommand.ExecuteAsync(null);
        main.FocusItems.Should().ContainSingle(t => t.Id == task.Id);

        (await NewService<NowService>().PickAsync(30, Energy.Medium))
            .Should().Contain(w => w.Task.Id == task.Id);
    }

    [Fact]
    public async Task Data_nadana_wrzutowi_nie_ginie_po_drodze()
    {
        // Usterka, przez którą „Dzisiaj" i „Plany" były puste, a zadanie nie pojawiało
        // się na siatce. Oba przejścia stanu wymagały, żeby zadanie miało już obszar —
        // a zadanie z wrzutu go nie ma. Data **znikała bez słowa**: zapis się udawał,
        // okno zamykało, i tyle.
        var main = NewService<MainViewModel>();
        main.CaptureText = "Zadzwonić do przedszkola";
        await main.CaptureCommand.ExecuteAsync(null);

        var capture = main.InboxItems.Single();
        capture.AreaId.Should().BeNull("wrzut nie ma obszaru i to jest cała trudność");

        var detail = NewService<TaskDetailViewModel>();
        await detail.LoadAsync(capture);

        var today = NewService<IClock>().Today;
        detail.DoDate = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        detail.DoTime = new TimeSpan(16, 0, 0);
        detail.EndTime = new TimeSpan(17, 30, 0);

        await detail.SaveAsync();

        detail.Problem.Should().BeNull("zapis miał się udać");

        var saved = await NewService<ITaskRepository>().FindAsync(capture.Id);

        saved!.DoDate.Should().Be(today);
        saved.DoTime.Should().Be(new TimeOnly(16, 0));
        saved.AreaId.Should().NotBeNull("zadanie z dniem wykonania musi gdzieś należeć");

        // Koniec nie jest osobnym polem: jest długością, i to ona rysuje blok.
        saved.EstimatedMinutes.Should().Be(90);

        // I dopiero to jest odpowiedź na „dlaczego Dzisiaj jest puste".
        await main.ShowTodayCommand.ExecuteAsync(null);
        main.TodayItems.Should().ContainSingle(w => w.Task.Id == capture.Id);

        // Na siatce też, o właściwej godzinie.
        var calendarId = NewService<CalendarViewModel>();
        await calendarId.LoadAsync();

        calendarId.Columns.SelectMany(k => k.Slots)
            .Should().ContainSingle(b => b.Title == "Zadzwonić do przedszkola")
            .Which.StartText.Should().Be("16:00");
    }

    [Fact]
    public async Task Zadanie_z_godzina_bez_konca_trwa_pol_godziny()
    {
        var task = await ZaplanowaneAsync("Przerwa");

        var detail = NewService<TaskDetailViewModel>();
        await detail.LoadAsync(task);
        detail.DoTime = new TimeSpan(9, 0, 0);
        detail.EndTime = null;
        detail.EstimatedMinutes = null;

        await detail.SaveAsync();

        var calendarId = NewService<CalendarViewModel>();
        await calendarId.LoadAsync();

        var block = calendarId.Columns.SelectMany(k => k.Slots).Single(b => b.Title == "Przerwa");

        block.StartText.Should().Be("09:00");
        block.EndText.Should().Be("09:30");
    }

    [Fact]
    public async Task Oszacowanie_z_przetwarzania_dociera_do_ekranu_teraz()
    {
        // Sedno: ekran „Teraz" dobiera pod dostępne minuty i poziom sił, więc zadanie
        // bez oszacowania nie trafia tam nigdy. Dotąd dało się to wpisać dopiero
        // w szczegółach — po przetworzeniu, z listy, drugim otwarciem.
        var main = NewService<MainViewModel>();

        main.CaptureText = "Zadzwonić do przychodni";
        await main.CaptureCommand.ExecuteAsync(null);

        var przetwarzanie = NewService<ClarifyViewModel>();
        await przetwarzanie.LoadAsync();

        przetwarzanie.Current.Should().NotBeNull();
        przetwarzanie.SelectedArea = przetwarzanie.Areas.First();
        przetwarzanie.EstimatedMinutes = 15;
        przetwarzanie.SelectedEnergy = EnergyLevelChoice.All.Single(e => e.Value == Energy.Low);

        await przetwarzanie.MakeNextCommand.ExecuteAsync(null);

        var now = main.Now;
        await now.LoadAsync();

        now.SelectedMinutes = MinutesChoice.All.First(m => m.Minutes >= 15);
        now.SelectedEnergy = EnergyChoice.All.Single(e => e.Value == Energy.Low);
        await now.RefreshCommand.ExecuteAsync(null);

        now.Picks.Should().Contain(w => w.Task.Title == "Zadzwonić do przychodni");
    }

    [Fact]
    public async Task Czynnosci_menu_podrecznego_robia_to_co_obiecuja()
    {
        // W menu mają być **tylko rzeczy, które działają**: pozycja, która nic nie robi,
        // uczy nieufności do całego menu, a nieufne menu przestaje być skrótem.
        var main = NewService<MainViewModel>();
        var today = NewService<IClock>().Today;

        var jutro = await ZaplanowaneAsync("Przełożyć");
        await main.PostponeTaskCommand.ExecuteAsync(jutro);

        (await NewService<ITaskRepository>().FindAsync(jutro.Id))!
            .DoDate.Should().Be(today.AddDays(1));

        var kosz = await ZaplanowaneAsync("Wyrzucić");
        await main.TrashTaskCommand.ExecuteAsync(kosz);

        (await NewService<ITaskRepository>().FindAsync(kosz.Id))!
            .State.Should().Be(TaskState.Trashed);

        var piatka = await ZaplanowaneAsync("Na dziś");
        await main.FocusTaskCommand.ExecuteAsync(piatka);

        main.FocusItems.Should().Contain(z => z.Id == piatka.Id);

        var detail = await ZaplanowaneAsync("Otworzyć");
        await main.OpenTaskCommand.ExecuteAsync(detail);

        NewService<TaskDetailViewModel>().IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Rytm_ustawiony_na_ekranie_rodzi_nastepne_zadanie()
    {
        // Trasa: lista wyboru w oknie → reguła → JSON w bazie → odczyt → następnik.
        // Sześć warstw, z których każda ma własny test i każdy przechodzi.
        var task = await ZaplanowaneAsync("Wynieść śmieci");

        var detail = NewService<TaskDetailViewModel>();
        detail.Load(task);
        detail.SelectedRepeat = RepeatChoice.All.Single(r => r.Kind == RecurrenceKind.Daily);

        await detail.SaveAsync();

        // Odczyt z bazy, nie z obiektu w pamięci: chodzi o to, czy reguła **przeżyła zapis**.
        var saved = await NewService<ITaskRepository>().FindAsync(task.Id);
        saved!.Recurrence.Should().NotBeNull();
        saved.Recurrence!.Kind.Should().Be(RecurrenceKind.Daily);

        var next = await NewService<TaskEditService>().CompleteAsync(task.Id);

        next.Should().NotBeNull();
        next!.Recurrence!.Kind.Should().Be(RecurrenceKind.Daily);
        next.Title.Should().Be("Wynieść śmieci");
    }

    [Fact]
    public async Task Praca_od_poniedzialku_do_piatku_dojezdza_do_bazy()
    {
        // Trasa: przycisk „dni robocze" → pięć przełączników → zbiór dni → JSON → odczyt.
        var task = await ZaplanowaneAsync("Praca");

        var detail = NewService<TaskDetailViewModel>();
        detail.Load(task);
        detail.SelectedRepeat = RepeatChoice.All.Single(r => r.Kind == RecurrenceKind.Weekly);
        detail.WorkdaysCommand.Execute(null);

        await detail.SaveAsync();

        var saved = await NewService<ITaskRepository>().FindAsync(task.Id);

        saved!.Recurrence!.DaysOfWeek.Should().Be(Weekdays.Workdays);
        saved.Recurrence.Until.Should().BeNull();
        saved.Recurrence.Count.Should().BeNull();
    }

    [Fact]
    public async Task Rozciagniecie_biezacego_bloku_nie_zmienia_dlugosci_serii()
    {
        // Objaw: zadanie niosące rytm jest jednocześnie **wystąpieniem** i **wzorcem
        // serii**, a zapowiedzi rysowane do przodu brały długość właśnie z niego.
        // Przeciągnięcie dolnej krawędzi dzisiejszego bloku zmieniało więc wszystkie.
        var task = await ZaplanowaneAsync("Praca");

        var detail = NewService<TaskDetailViewModel>();
        await detail.LoadAsync(task);
        detail.SelectedRepeat = RepeatChoice.All.Single(r => r.Kind == RecurrenceKind.Weekly);
        detail.WorkdaysCommand.Execute(null);
        detail.EstimatedMinutes = 480;

        await detail.SaveAsync();

        var tasks = NewService<ITaskRepository>();
        (await tasks.FindAsync(task.Id))!.Recurrence!.Minutes
            .Should().Be(480, "długość wpisana w karcie jest decyzją o całej serii");

        // Siatka: dziś siedzę sześć godzin, a nie osiem.
        await NewService<TaskEditService>().SetMinutesAsync(task.Id, 360);

        var after = await tasks.FindAsync(task.Id);

        after!.EstimatedMinutes.Should().Be(360, "to wystąpienie jest krótsze");
        after.Recurrence!.Minutes.Should().Be(480, "seria zostaje przy swojej długości");
    }

    [Fact]
    public async Task Rytm_bez_zapamietanej_dlugosci_zapamietuje_ja_przy_pierwszym_rozciagnieciu()
    {
        // Rytmy założone, zanim seria umiała pamiętać własną długość, mają to pole puste
        // i długość bierze się wtedy z wystąpienia niosącego regułę. Pierwsze rozciągnięcie
        // musi więc najpierw zapisać to, czym seria była do tej pory.
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();

        task.SetEstimate(480, task.Energy, hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday), hlc.Next());

        await NewService<IUnitOfWork>().SaveChangesAsync();

        await NewService<TaskEditService>().SetMinutesAsync(task.Id, 360);

        var after = await NewService<ITaskRepository>().FindAsync(task.Id);

        after!.EstimatedMinutes.Should().Be(360);
        after.Recurrence!.Minutes.Should().Be(480);
    }

    [Fact]
    public async Task Przelozenie_biezacego_bloku_nie_zmienia_pory_serii()
    {
        // To samo, co przy długości: zadanie niosące rytm jest jednocześnie wystąpieniem
        // i wzorcem serii, więc przeciągnięcie dzisiejszego bloku o godzinę w dół
        // przestawiało wszystkie zapowiedzi naraz.
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();
        var clock = NewService<IClock>();

        task.SetDoTime(new TimeOnly(9, 0), hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Workdays), hlc.Next());

        await NewService<IUnitOfWork>().SaveChangesAsync();

        // Siatka: dziś zaczynam o dziesiątej.
        await NewService<TaskEditService>()
            .RescheduleAsync(task.Id, clock.Today, new TimeOnly(10, 0));

        var after = await NewService<ITaskRepository>().FindAsync(task.Id);

        after!.DoTime.Should().Be(new TimeOnly(10, 0), "to wystąpienie zaczyna się później");
        after.Recurrence!.Time.Should().Be(new TimeOnly(9, 0), "rytm zostaje przy swojej porze");
    }

    [Fact]
    public async Task Zapis_karty_nie_przepisuje_na_rytm_wyjatku_z_siatki()
    {
        // Karta pokazuje porę i długość **tego wystąpienia**, a po przeciągnięciu po
        // siatce bywają one inne niż w serii. Zapis karty — choćby po poprawieniu samego
        // tytułu — przepisywał je na cały rytm, czyli cofał to, co siatka właśnie zrobiła.
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();
        var clock = NewService<IClock>();

        task.SetDoTime(new TimeOnly(9, 0), hlc.Next());
        task.SetEstimate(480, task.Energy, hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Workdays), hlc.Next());

        await NewService<IUnitOfWork>().SaveChangesAsync();

        // Siatka: dziś zaczynam o dziesiątej.
        await NewService<TaskEditService>()
            .RescheduleAsync(task.Id, clock.Today, new TimeOnly(10, 0));

        var tasks = NewService<ITaskRepository>();
        var detail = NewService<TaskDetailViewModel>();

        await detail.LoadAsync((await tasks.FindAsync(task.Id))!);
        detail.Title = "Praca zdalna";

        await detail.SaveAsync();

        var saved = await tasks.FindAsync(task.Id);

        saved!.Title.Should().Be("Praca zdalna");
        saved.DoTime.Should().Be(new TimeOnly(10, 0), "to wystąpienie zostaje o dziesiątej");
        saved.Recurrence!.Time.Should().Be(new TimeOnly(9, 0), "rytm zostaje przy dziewiątej");
    }

    [Fact]
    public async Task Pora_ruszona_w_karcie_jest_decyzja_o_calej_serii()
    {
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();

        task.SetDoTime(new TimeOnly(9, 0), hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(
                RecurrenceKind.Weekly, daysOfWeek: Weekdays.Workdays, time: new TimeOnly(9, 0)),
            hlc.Next());

        await NewService<IUnitOfWork>().SaveChangesAsync();

        var tasks = NewService<ITaskRepository>();
        var detail = NewService<TaskDetailViewModel>();

        await detail.LoadAsync((await tasks.FindAsync(task.Id))!);
        detail.DoTime = new TimeSpan(11, 0, 0);

        await detail.SaveAsync();

        var saved = await tasks.FindAsync(task.Id);

        saved!.DoTime.Should().Be(new TimeOnly(11, 0));
        saved.Recurrence!.Time.Should().Be(new TimeOnly(11, 0), "w karcie mówi się o serii");
    }

    [Fact]
    public async Task Karta_zapowiedzi_pokazuje_jej_wlasne_godziny_i_zapisuje_je_przy_dniu()
    {
        // Objaw: kliknięcie w zapowiedź otwierało kartę **zadania niosącego rytm**, więc
        // po przeciągnięciu jednego dnia karta mówiła co innego niż blok, z którego się
        // ją otwierało — pokazywała godziny całej serii.
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();
        var today = NewService<IClock>().Today;

        task.SetDoTime(new TimeOnly(9, 0), hlc.Next());
        task.SetEstimate(480, task.Energy, hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(
                RecurrenceKind.Daily, time: new TimeOnly(9, 0), minutes: 480),
            hlc.Next());

        await NewService<IUnitOfWork>().SaveChangesAsync();

        var when = today.AddDays(3);
        var calendarId = NewService<CalendarViewModel>();

        // Siatka wczytana przed otwarciem karty: zapis kończy się jej przeliczeniem,
        // a model bez wczytania stoi na dacie zerowej i nie ma czego przeliczać.
        await calendarId.LoadAsync();

        // Blok taki, jaki siatka rysuje dla wystąpienia przełożonego na dziesiątą.
        var box = new SlotBox(
            "Praca", 0, 0, 0, 0, IsTask: true, Color: null,
            StartText: "10:00", EndText: "12:00", TaskId: null,
            DayText: when.ToString("dd.MM.yyyy"), SourceId: null, ExternalId: null,
            IsDone: false, CanWrite: false, RhythmId: task.Id, RhythmDate: when);

        calendarId.OpenTaskCommand.Execute(box);

        calendarId.IsOpenedAhead.Should().BeTrue("to jest karta jednego wystąpienia");
        calendarId.OpenedStart.Should().Be(new TimeSpan(10, 0, 0), "godziny z bloku, nie z serii");
        calendarId.OpenedEnd.Should().Be(new TimeSpan(12, 0, 0));

        calendarId.OpenedStart = new TimeSpan(11, 0, 0);
        calendarId.OpenedEnd = new TimeSpan(13, 30, 0);

        await calendarId.SaveOpenedCommand.ExecuteAsync(null);

        var change = (await NewService<ITaskRepository>().FindAsync(task.Id))!
            .Recurrence!.ChangeOn(when);

        change!.Time.Should().Be(new TimeOnly(11, 0));
        change.Minutes.Should().Be(150);
        change.Day.Should().Be(when);
    }

    [Fact]
    public async Task Zapis_karty_zapowiedzi_nie_rusza_rytmu()
    {
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();
        var today = NewService<IClock>().Today;

        task.SetDoTime(new TimeOnly(9, 0), hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Daily, time: new TimeOnly(9, 0), minutes: 480),
            hlc.Next());

        await NewService<IUnitOfWork>().SaveChangesAsync();

        var when = today.AddDays(2);

        await NewService<TaskEditService>()
            .SetOccurrenceAsync(task.Id, when, when, new TimeOnly(11, 0), 150);

        var saved = (await NewService<ITaskRepository>().FindAsync(task.Id))!;

        saved.Recurrence!.Time.Should().Be(new TimeOnly(9, 0), "rytm zostaje przy swojej porze");
        saved.Recurrence.Minutes.Should().Be(480, "i przy swojej długości");
        saved.DoTime.Should().Be(new TimeOnly(9, 0), "bieżące wystąpienie też nietknięte");
    }

    [Fact]
    public async Task Przypomnienie_wpisane_w_karcie_jest_cecha_rytmu()
    {
        // Trasa: pola wyprzedzeń w karcie → reguła → JSON → odczyt → kolejne wystąpienie.
        // Bez reguły przypomnienie szło łańcuchem kopii z wystąpienia na wystąpienie,
        // a łańcuch kopii gubi się bezpowrotnie na pierwszym pękniętym ogniwie.
        var task = await ZaplanowaneAsync("Praca");

        var detail = NewService<TaskDetailViewModel>();
        await detail.LoadAsync(task);
        detail.DoTime = new TimeSpan(9, 0, 0);
        detail.SelectedRepeat = RepeatChoice.All.Single(r => r.Kind == RecurrenceKind.Daily);

        foreach (var lead in detail.Leads.Where(w => w.Minutes is 30))
        {
            lead.IsChecked = true;
        }

        await detail.SaveAsync();

        var tasks = NewService<ITaskRepository>();
        var saved = await tasks.FindAsync(task.Id);

        saved!.Recurrence!.Leads.Should().Contain(30, "rytm niesie przypomnienie");

        var next = await NewService<TaskEditService>().CompleteAsync(task.Id);

        next!.ReminderLeads.Should().Contain(30, "i oddaje je kolejnemu wystąpieniu");
    }

    [Fact]
    public async Task Zapowiedz_wyjeta_z_serii_staje_sie_zwyklym_zadaniem()
    {
        // Zapowiedź nie jest zadaniem, więc nie ma przypomnienia, nie da się jej pokazać
        // osobie ani dopisać do niej notatki. Wyjęcie zamienia ten jeden dzień w zwykłe
        // zadanie — z porą i długością, które miało na siatce — a seria przestaje je
        // produkować.
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();
        var today = NewService<IClock>().Today;

        task.SetDoTime(new TimeOnly(9, 0), hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(
                RecurrenceKind.Daily, time: new TimeOnly(9, 0), minutes: 480, leads: [30]),
            hlc.Next());

        await NewService<IUnitOfWork>().SaveChangesAsync();

        var when = today.AddDays(3);
        var edit = NewService<TaskEditService>();

        // Wystąpienie przełożone przed wyjęciem: zadanie ma stanąć tam, gdzie stała
        // zapowiedź, a nie tam, gdzie każe reguła.
        await edit.MoveOccurrenceAsync(task.Id, when, when, new TimeOnly(11, 0));

        var alone = await edit.DetachOccurrenceAsync(task.Id, when);

        alone!.Title.Should().Be("Praca");
        alone.DoDate.Should().Be(when);
        alone.DoTime.Should().Be(new TimeOnly(11, 0), "pora z zapowiedzi, nie z reguły");
        alone.EstimatedMinutes.Should().Be(480, "długość seria pamięta");
        alone.ReminderLeads.Should().Equal(30, "przypomnienie też jest cechą rytmu");
        alone.Recurrence.Should().BeNull("wyjęte zadanie nie niesie rytmu");

        var rhythm = await NewService<ITaskRepository>().FindAsync(task.Id);

        rhythm!.Recurrence!.ChangeOn(when)!.Dropped
            .Should().BeTrue("seria przestaje produkować ten dzień");
    }

    [Fact]
    public async Task Dlugosc_jednej_zapowiedzi_zapisuje_sie_przy_jej_dniu()
    {
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();
        var today = NewService<IClock>().Today;

        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Daily, minutes: 480), hlc.Next());

        await NewService<IUnitOfWork>().SaveChangesAsync();

        var edit = NewService<TaskEditService>();
        var when = today.AddDays(3);

        // Najpierw przełożenie, potem długość: wpis jest jeden na dzień, więc drugie
        // nie ma prawa cofnąć pierwszego.
        await edit.MoveOccurrenceAsync(task.Id, when, when.AddDays(1), new TimeOnly(10, 0));
        await edit.ResizeOccurrenceAsync(task.Id, when, 120);

        var change = (await NewService<ITaskRepository>().FindAsync(task.Id))!
            .Recurrence!.ChangeOn(when);

        change!.Minutes.Should().Be(120);
        change.Day.Should().Be(when.AddDays(1), "przełożenie przetrwało dopisanie długości");
        change.Time.Should().Be(new TimeOnly(10, 0));
    }

    [Fact]
    public async Task Zapis_karty_nie_kasuje_zmian_pojedynczych_wystapien()
    {
        // Karta odpowiada na pytanie „czym ta rzecz jest", a nie „co się stanie z tą
        // jedną środą". Składanie reguły od nowa gubiło zmiany przy każdym zapisie
        // czegokolwiek innego — także zmianie samego tytułu.
        var task = await ZaplanowaneAsync("Praca");
        var hlc = NewService<IHlcSource>();
        var today = NewService<IClock>().Today;

        task.SetRecurrence(new RecurrenceRule(RecurrenceKind.Daily), hlc.Next());
        await NewService<IUnitOfWork>().SaveChangesAsync();

        var when = today.AddDays(2);
        await NewService<TaskEditService>().DropOccurrenceAsync(task.Id, when);

        var tasks = NewService<ITaskRepository>();
        var detail = NewService<TaskDetailViewModel>();

        await detail.LoadAsync((await tasks.FindAsync(task.Id))!);
        detail.Title = "Praca zdalna";

        await detail.SaveAsync();

        var saved = await tasks.FindAsync(task.Id);

        saved!.Title.Should().Be("Praca zdalna");
        saved.Recurrence!.ChangeOn(when).Should().NotBeNull("odwołanie przetrwało zapis karty");
        saved.Recurrence.ChangeOn(when)!.Dropped.Should().BeTrue();
    }

    [Fact]
    public async Task Koniec_rytmu_po_wystapieniach_i_po_dacie_dojezdza_do_bazy()
    {
        var task = await ZaplanowaneAsync("Kurs");

        var detail = NewService<TaskDetailViewModel>();
        detail.Load(task);
        detail.SelectedRepeat = RepeatChoice.All.Single(r => r.Kind == RecurrenceKind.Weekly);
        detail.Monday = true;
        detail.SelectedEnd = EndChoice.All.Single(k => k.Value == RepeatEnd.AfterCount);
        detail.Occurrences = 5;

        await detail.SaveAsync();

        var tasks = NewService<ITaskRepository>();
        var afterCount = await tasks.FindAsync(task.Id);

        afterCount!.Recurrence!.Count.Should().Be(5);
        afterCount.Recurrence.Until.Should().BeNull("odpowiedź jest jedna, nie dwie");

        // Ta sama karta, druga odpowiedź: data zastępuje liczbę, a nie dokłada się do niej.
        detail.Load(afterCount);
        detail.SelectedEnd = EndChoice.All.Single(k => k.Value == RepeatEnd.OnDate);
        detail.RhythmEnd = new DateTimeOffset(new DateTime(2027, 1, 31), TimeSpan.Zero);

        await detail.SaveAsync();

        var afterDate = await tasks.FindAsync(task.Id);

        afterDate!.Recurrence!.Until.Should().Be(new DateOnly(2027, 1, 31));
        afterDate.Recurrence.Count.Should().BeNull();
    }

    [Fact]
    public async Task Wybrany_koniec_bez_wartosci_nie_zapisuje_sie_po_cichu()
    {
        // Puste pole jest brakiem odpowiedzi, a nie odpowiedzią „nieważne". Zapisane
        // jako „bez końca" znaczyłoby coś innego, niż mówi lista nad polem.
        var task = await ZaplanowaneAsync("Kurs");

        var detail = NewService<TaskDetailViewModel>();
        detail.Load(task);
        detail.SelectedRepeat = RepeatChoice.All.Single(r => r.Kind == RecurrenceKind.Weekly);
        detail.Monday = true;
        detail.SelectedEnd = EndChoice.All.Single(k => k.Value == RepeatEnd.AfterCount);

        detail.Summary.Should().Contain("po ilu");

        await detail.SaveAsync();

        (await NewService<ITaskRepository>().FindAsync(task.Id))!
            .Recurrence.Should().BeNull("zapis z brakującą odpowiedzią nie doszedł do skutku");
    }

    [Fact]
    public async Task Przypomnienie_z_dnia_i_pory_dojezdza_do_bazy()
    {
        // Dzień i pora są w oknie osobno, a w bazie są jedną chwilą. Składanie dzieje
        // się przy zapisie i jest to dokładnie ten rodzaj miejsca, w którym wartość
        // znika bez śladu — sama pora bez dnia nie znaczy nic i ma nie zapisać niczego.
        var task = await ZaplanowaneAsync("Zadzwonić do przychodni");

        var detail = NewService<TaskDetailViewModel>();
        detail.Load(task);
        detail.ReminderTime = new TimeSpan(14, 30, 0);

        await detail.SaveAsync();

        (await NewService<ITaskRepository>().FindAsync(task.Id))!
            .ReminderAt.Should().BeNull("sama pora bez dnia nie wskazuje chwili");

        detail.Load(task);
        detail.ReminderDay = new DateTimeOffset(
            NewService<IClock>().Today.ToDateTime(TimeOnly.MinValue), NewService<IClock>().Now.Offset);
        detail.ReminderTime = new TimeSpan(14, 30, 0);

        await detail.SaveAsync();

        var saved = await NewService<ITaskRepository>().FindAsync(task.Id);

        saved!.ReminderAt.Should().NotBeNull();
        saved.ReminderAt!.Value.TimeOfDay.Should().Be(new TimeSpan(14, 30, 0));
    }

    [Fact]
    public async Task Wyprzedzenia_z_okna_dojezdzaja_do_bazy()
    {
        // Kwadraciki w oknie, lista minut w bazie, chwile w przypomnieniach — trzy
        // różne kształty tej samej rzeczy. Test idzie przez wszystkie trzy, bo każde
        // przejście jest miejscem, w którym wyprzedzenie może cicho zniknąć.
        var task = await ZaplanowaneAsync("Wizyta u lekarza");

        var detail = NewService<TaskDetailViewModel>();
        await detail.LoadAsync(task);

        detail.HasTime.Should().BeFalse("zadanie jeszcze nie ma godziny");

        detail.DoTime = new TimeSpan(14, 0, 0);

        detail.HasTime.Should().BeTrue();
        detail.Leads.Single(w => w.Minutes == 0).IsChecked
            .Should().BeTrue("wpisana godzina sama włącza przypomnienie o czasie");

        detail.Leads.Single(w => w.Minutes == 30).IsChecked = true;

        // Dowolnie: dwie godziny. Ma wskoczyć na listę na swoje miejsce w kolejności
        // czasu i od razu zostać zaznaczone.
        detail.CustomLead = 2;
        detail.SelectedLeadUnit = LeadUnitChoice.All.Single(j => j.Label == "godzin");
        detail.AddLeadCommand.Execute(null);

        var dopisane = detail.Leads.Single(w => w.Minutes == 120);
        dopisane.IsChecked.Should().BeTrue();
        dopisane.Label.Should().Be("2 godz. wcześniej");
        detail.Leads.Select(w => w.Minutes).Should().BeInAscendingOrder();

        await detail.SaveAsync();

        var saved = await NewService<ITaskRepository>().FindAsync(task.Id);
        saved!.ReminderLeads.Should().Equal(0, 30, 120);

        // Droga powrotna: okno otwarte drugi raz ma pokazać to samo.
        detail.Load(saved);
        detail.Leads.Where(w => w.IsChecked).Select(w => w.Minutes)
            .Should().Equal(new[] { 0, 30, 120 }, "wczytanie ma pokazać to samo, co się zapisało");

        // Zabrana godzina zabiera to, od czego wyprzedzenia się liczyły — więc i je.
        detail.DoTime = null;

        detail.Leads.Should().NotContain(
            w => w.IsChecked, "okno ma pokazywać to, co się zapisze");

        await detail.SaveAsync();

        (await NewService<ITaskRepository>().FindAsync(task.Id))!
            .ReminderLeads.Should().BeEmpty("bez godziny nie ma od czego liczyć");
    }

    [Fact]
    public async Task Wziete_na_dzis_widac_na_pasku_calodniowym()
    {
        // Wybór na dziś nie ustawia dnia wykonania — ustawia obietnicę. Zadanie było
        // przez to niewidoczne w kalendarzu, czyli w jedynym miejscu, gdzie widać cały
        // dzień naraz.
        var task = await ZaplanowaneAsync("Zadzwonić do przedszkola");

        // Bez dnia wykonania: samo wzięcie na dziś ma wystarczyć.
        var tasks = NewService<ITaskRepository>();
        task.MoveDoDate(null, NewService<IHlcSource>().Next());
        await NewService<IUnitOfWork>().SaveChangesAsync();

        var calendarId = NewService<CalendarViewModel>();
        await calendarId.LoadAsync();

        calendarId.Columns.SelectMany(k => k.AllDay).Should()
            .NotContain(b => b.Title == task.Title, "jeszcze nie zostało wzięte");

        (await NewService<FocusService>().TryFocusAsync(task.Id)).Accepted.Should().BeTrue();

        await calendarId.LoadAsync();

        calendarId.Columns.SelectMany(k => k.AllDay).Should()
            .Contain(b => b.Title == task.Title, "wzięte na dziś należy do dnia");

        // I tylko raz — zadanie z dniem wykonania **i** wyborem nie ma stać w dwóch
        // miejscach naraz.
        (await tasks.FindAsync(task.Id))!.DoDate.Should().BeNull();
    }

    [Fact]
    public async Task Zapisany_filtr_wraca_z_bazy_z_tymi_samymi_warunkami()
    {
        // Warunki jadą do bazy jako JSON i wracają do **innego** modelu widoku niż ten,
        // który je zapisał. Warstwa po warstwie wszystko przechodzi także wtedy, gdy
        // jedno pole gubi się przy zapisie albo przy odczycie.
        await ZaplanowaneAsync("Zrobić przelew");

        var filters = NewService<FiltersViewModel>();
        await filters.LoadAsync();

        filters.Name = "Na dziś";
        filters.Text = "przelew";

        await filters.SaveCommand.ExecuteAsync(null);
        filters.OpenId.Should().NotBe(Guid.Empty, "zapis miał zwrócić identyfikator");

        var saved = (await NewService<ISavedFilterRepository>().AllAsync())
            .Single(f => f.Name == "Na dziś");

        saved.Query.Should().NotBeNull("definicja ma się dać odczytać z powrotem");
        saved.Query!.Conditions.Should().NotBeEmpty("warunek „przelew” miał przeżyć zapis");
    }

    [Fact]
    public async Task Wstecz_wraca_na_poprzedni_ekran_zamiast_zamykac_aplikacje()
    {
        // Objaw: z widoku „Co się działo" przycisk wstecz zamykał aplikację. Zamknięcie
        // w odpowiedzi na cofnięcie z ekranu, na który się przed chwilą weszło, wygląda
        // jak awaria, a nie jak nawigacja.
        var main = NewService<MainViewModel>();

        await main.ShowNotesCommand.ExecuteAsync(null);
        await main.ShowJournalCommand.ExecuteAsync(null);

        main.CanGoBack.Should().BeTrue();
        await main.BackAsync();

        main.IsNotes.Should().BeTrue("cofnięcie ma wrócić tam, skąd się przyszło");
    }

    [Fact]
    public async Task Wstecz_idzie_sladem_wstecz_a_nie_w_kolko_miedzy_dwoma_ekranami()
    {
        // Bez znacznika „trwa cofanie" samo cofnięcie dopisywałoby do śladu ekran,
        // z którego się cofa — i drugie cofnięcie wracałoby tam, skąd się właśnie
        // przyszło. Przycisk wstecz byłby wtedy przełącznikiem, nie cofnięciem.
        var main = NewService<MainViewModel>();

        await main.ShowNotesCommand.ExecuteAsync(null);
        await main.ShowCalendarCommand.ExecuteAsync(null);
        await main.ShowJournalCommand.ExecuteAsync(null);

        await main.BackAsync();
        main.IsCalendar.Should().BeTrue();

        await main.BackAsync();
        main.IsNotes.Should().BeTrue("drugie cofnięcie ma iść o jeden dalej wstecz");
    }

    [Fact]
    public async Task Wstecz_z_pustego_sladu_wraca_na_ekran_domowy_i_dopiero_stamtad_oddaje()
    {
        // Ekranem domowym jest **kalendarz**, bo to on jest wartością początkową ekranu
        // i pierwszym pytaniem dnia. Pisałem to najpierw na „Dzisiaj" i test to wyłapał:
        // cofanie kończyło się gdzie indziej, niż mówiło o tym pytanie „czy jest dokąd".
        //
        // Dopiero stojąc na ekranie domowym oddajemy cofnięcie systemowi — przycisk
        // wstecz, który nigdy nie wychodzi z aplikacji, przestaje być przyciskiem wstecz.
        var main = NewService<MainViewModel>();

        main.IsCalendar.Should().BeTrue("aplikacja otwiera się na kalendarzu");

        await main.ShowJournalCommand.ExecuteAsync(null);
        await main.BackAsync();

        main.Current.Should().Be(Screen.Calendar);
        main.CanGoBack.Should().BeFalse("z ekranu domowego cofnięcie należy do systemu");
    }

    public void Dispose()
    {
        _services.Dispose();

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
