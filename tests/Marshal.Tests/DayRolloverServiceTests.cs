using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
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
/// Przejście dnia na prawdziwej bazie: to tu widać, czy zapytanie wybiera właściwe
/// zadania i czy powtórne wołanie naprawdę nic nie dokłada.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wystąpienie serii nie przesuwa się na dziś</b> i to jest tu rzecz najważniejsza.
/// W modelu z zapisanym oknem dzień jest częścią tożsamości wiersza — identyfikator
/// liczy się z serii i z niego — więc przeniesienie zaległego postawiłoby wiersz na
/// dniu, który seria sama ma obsadzić. Tą drogą wracały „po dwa wystąpienia na dzień",
/// tylko wejściem od innej strony.
/// </para>
/// <para>
/// Odpowiedź na minięcie różni się więc wreszcie czymś, co widać: przepadło i nie wraca,
/// przypomina do następnego razu, albo zostaje na zawsze jako zaległość.
/// </para>
/// </remarks>
public sealed class DayRolloverServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly DayRolloverService _service;
    private readonly SeriesService _rhythms;
    private readonly TaskSeriesRepository _series;
    private readonly HlcSource _hlc;
    private readonly Guid _area = Guid.CreateVersion7();

    public DayRolloverServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();

        // Jeden zegar logiczny na urządzenie, tak jak w aplikacji. Dwa niezależne
        // ruszające od zera wydałyby te same znaczniki dwa razy i drugi zapis zostałby
        // odrzucony jako cofnięcie zegara — co jest zachowaniem prawidłowym.
        _hlc = new HlcSource(_clock, "biurko");
        _series = new TaskSeriesRepository(_db);

        var store = new TaskRepository(_db);
        var work = new UnitOfWork(_db);

        _rhythms = new SeriesService(_series, store, work, _clock, _hlc);
        _service = new DayRolloverService(store, _series, _rhythms, work, _clock, _hlc);
    }

    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    private TaskItem Add(string title, string doDate)
    {
        var task = TaskItem.Capture(title, _clock.Now, _hlc.Next());
        task.Schedule(_area, D(doDate), _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    /// <summary>
    /// Seria założona w dniu <paramref name="from"/> — z zegarem przestawionym tam
    /// i z powrotem.
    /// </summary>
    /// <remarks>
    /// Okno stawia dni **od dziś w przód**: historia ma zostać taka, jaka była, a nie
    /// dorosnąć wstecz o wystąpienia, których nigdy nie było. Seria mająca zaległe dni
    /// musi więc zostać założona wtedy, kiedy te dni były jeszcze przyszłością — i tak
    /// też dzieje się w życiu.
    /// </remarks>
    private async Task<TaskSeries> Series(string title, string from, OnMissed onMissed)
    {
        var was = _clock.Now;
        _clock.Now = new DateTimeOffset(D(from).ToDateTime(new TimeOnly(9, 0)), was.Offset);

        var first = Add(title, from);
        var rule = new RecurrenceRule(RecurrenceKind.Daily, onMissed: onMissed);
        var one = await _rhythms.StartAsync(first, rule);

        // Okno schodzi partiami, a te testy pytają o dni, które mają już stać.
        for (var guard = 0; guard < 20; guard++)
        {
            if ((await _rhythms.TopUpAsync(one)).Added == 0)
            {
                break;
            }
        }

        _clock.Now = was;

        return one;
    }

    private List<TaskItem> Occurrences(TaskSeries one) =>
        [.. _db.Tasks.Where(t => t.SeriesId == one.Id).OrderBy(t => t.DoDate)];

    [Fact]
    public async Task Pusta_baza_nie_ma_czego_przesuwac()
    {
        (await _service.RunAsync()).Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Zalegle_zaplanowane_laduja_na_dzis()
    {
        var id = Add("Zadzwonić", "2026-09-10").Id;

        var report = await _service.RunAsync();

        report.Moved.Should().Be(1);
        _db.Tasks.Single(t => t.Id == id).DoDate.Should().Be(D("2026-09-16"));
    }

    [Fact]
    public async Task Zadanie_ze_skrzynki_nie_jest_ruszane()
    {
        // Skrzynka nie ma dnia wykonania i nie powinna go dostać z przypadku.
        var capture = TaskItem.Capture("do przemyślenia", _clock.Now, _hlc.Next());
        _db.Tasks.Add(capture);
        _db.SaveChanges();

        (await _service.RunAsync()).Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Wystapienie_serii_zostaje_na_swoim_dniu()
    {
        // Sedno przebudowy. Dzień jest częścią tożsamości wiersza, więc przeniesienie
        // zaległego na dziś postawiłoby go na dniu, który seria sama obsadza — i tą
        // drogą wracałyby dwa wystąpienia na jeden dzień.
        var one = await Series("Zapłacić", "2026-09-14", OnMissed.Accumulate);

        await _service.RunAsync();

        var missed = Occurrences(one).Single(z => z.DoDate == D("2026-09-14"));

        missed.DoDate.Should().Be(D("2026-09-14"), "zaległe zostaje tam, gdzie było");
        missed.State.Should().Be(TaskState.Scheduled);

        Occurrences(one).Count(z => z.DoDate == D("2026-09-16"))
            .Should().Be(1, "dzisiejsze jest jedno, a nie dwa");
    }

    [Fact]
    public async Task Skip_zamyka_przegapione_od_razu()
    {
        var one = await Series("Wynieść śmieci", "2026-09-09", OnMissed.Skip);

        await _service.RunAsync();

        var days = Occurrences(one);

        days.Where(z => z.DoDate < D("2026-09-16"))
            .Should().OnlyContain(z => z.State == TaskState.Trashed);

        days.Single(z => z.DoDate == D("2026-09-16")).State
            .Should().Be(TaskState.Scheduled, "dzisiejsze żyje");
    }

    [Fact]
    public async Task Carry_pyta_do_nastepnego_razu_i_przestaje()
    {
        // „Co poniedziałek śmieci" ma pytać przez tydzień i przestać, kiedy przychodzi
        // następny poniedziałek: dwa wystąpienia tej samej rzeczy nie są dwiema rzeczami.
        var one = await Series("Wynieść śmieci", "2026-09-14", OnMissed.Carry);

        await _service.RunAsync();

        var days = Occurrences(one);

        // Przedwczorajsze i wczorajsze przepadły, bo rytm wypadł od tamtej pory znowu.
        days.Single(z => z.DoDate == D("2026-09-14")).State.Should().Be(TaskState.Trashed);
        days.Single(z => z.DoDate == D("2026-09-15")).State.Should().Be(TaskState.Trashed);

        // Dzisiejsze żyje i nic go nie zastąpiło.
        days.Single(z => z.DoDate == D("2026-09-16")).State.Should().Be(TaskState.Scheduled);
    }

    [Fact]
    public async Task Accumulate_zostawia_kazda_zaleglosc()
    {
        // Trzy opuszczone treningi to trzy treningi, których nie było.
        var one = await Series("Trening", "2026-09-13", OnMissed.Accumulate);

        await _service.RunAsync();

        Occurrences(one)
            .Where(z => z.DoDate < D("2026-09-16"))
            .Should().HaveCount(3).And.OnlyContain(z => z.State == TaskState.Scheduled);
    }

    [Fact]
    public async Task Drugie_uruchomienie_tego_samego_dnia_nic_nie_zmienia()
    {
        // Warunek, nie wygoda: dwa urządzenia robią to samo, nie umawiając się, które ma.
        Add("Zadzwonić", "2026-09-10");
        await Series("Podlać", "2026-09-12", OnMissed.Carry);

        await _service.RunAsync();
        var second = await _service.RunAsync();

        second.Should().Be(new RolloverReport(0, 0));
    }

    [Fact]
    public async Task Przejscie_dnia_dopelnia_okno_serii()
    {
        var one = await Series("Podlać", "2026-09-16", OnMissed.Carry);

        var before = Occurrences(one).Count;


        // Dzień dalej: okno ma się przesunąć o jeden, a nie stać w miejscu.
        _clock.Now = _clock.Now.AddDays(1);

        var report = await _service.RunAsync();

        report.Spawned.Should().Be(1);
        Occurrences(one).Count.Should().Be(before + 1);
    }

    [Fact]
    public async Task Przejscie_dnia_nie_rusza_przed_scaleniem()
    {
        // Zaraz po uruchomieniu to urządzenie nie wie nic o wczorajszym dniu spędzonym
        // z telefonem w ręku — a sądy przejścia dnia są nieodwracalne i wygrywają potem
        // scalanie, bo niosą świeższy znacznik niż odhaczenie.
        var id = Add("Zadzwonić", "2026-09-10").Id;

        var gate = new SyncState();

        var waiting = new DayRolloverService(
            new TaskRepository(_db), _series, _rhythms, new UnitOfWork(_db),
            _clock, _hlc, null, gate);

        (await waiting.RunAsync()).Deferred.Should().BeTrue();
        _db.Tasks.Single(t => t.Id == id).DoDate
            .Should().Be(D("2026-09-10"), "dopóki nie wiadomo, nic się nie rusza");

        gate.Settle();

        (await waiting.RunAsync()).Moved.Should().Be(1);
        _db.Tasks.Single(t => t.Id == id).DoDate.Should().Be(D("2026-09-16"));
    }

    [Fact]
    public async Task Odhaczone_a_przeniesione_wraca_na_swoj_dzien()
    {
        // Objaw: zadanie odhaczone wieczorem na telefonie, rano na pulpicie stoi na dziś.
        // Przejście dnia zdążyło przed scaleniem i przeniosło je jako zaległe, a scalanie
        // idzie po polach: stan wygrało odhaczenie, dzień wykonania — przeniesienie.
        var task = Add("Zadzwonić", "2026-09-15");

        task.RollTo(D("2026-09-16"), _hlc.Next());
        task.Complete(Evening("2026-09-15"), _hlc.Next());
        _db.SaveChanges();

        (await _service.RunAsync()).Mended.Should().Be(1);

        var mended = _db.Tasks.Single(t => t.Id == task.Id);

        mended.DoDate.Should().Be(D("2026-09-15"), "zrobione w poniedziałek zostaje w poniedziałek");
        mended.State.Should().Be(TaskState.Done);
    }

    [Fact]
    public async Task Odhaczone_wystapienie_z_nagrobkiem_wraca()
    {
        // Ten sam wyścig, drugi kształt: lokalnie wczorajsze wystąpienie wyglądało na
        // przegapione, a dzisiejsze już stało — więc przejście dnia postawiło na nim
        // nagrobek. Po scaleniu zostaje dzień odhaczony i skasowany naraz.
        var one = await Series("Wynieść śmieci", "2026-09-15", OnMissed.Carry);

        var yesterday = Occurrences(one).Single(z => z.DoDate == D("2026-09-15"));

        yesterday.Complete(Evening("2026-09-15"), _hlc.Next());
        yesterday.Trash(_hlc.Next());
        _db.SaveChanges();

        (await _service.RunAsync()).Mended.Should().Be(1);

        _db.Tasks.Single(t => t.Id == yesterday.Id).State
            .Should().Be(TaskState.Done, "odhaczenie jest zapisem tego, co się wydarzyło");
    }

    private DateTimeOffset Evening(string iso) =>
        new(D(iso).ToDateTime(new TimeOnly(20, 0)), _clock.Now.Offset);

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
