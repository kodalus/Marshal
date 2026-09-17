using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Areas;
using Marshal.Domain.Calendar;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Calendar;
using Marshal.Infrastructure.Data;
using Marshal.Application.UseCases;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Marshal.UI.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Kopia kalendarzy i składanie siatki (spec 10.1, 11).
/// </summary>
public sealed class CalendarStoreTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    /// <summary>Kanał, który oddaje to, co mu się włoży. Bez sieci i bez niespodzianek.</summary>
    private sealed class Kanal(CalendarKind kind) : ICalendarFeed
    {
        public FeedResult Next { get; set; } = new([], null, true);

        public int Wywolan { get; private set; }

        public bool Rzuca { get; set; }

        public CalendarKind Kind => kind;

        public Task<FeedResult> FetchAsync(
            CalendarSource source, string? syncToken, CancellationToken ct = default)
        {
            Wywolan++;

            return Rzuca
                ? throw new HttpRequestException("kanał nie odpowiada")
                : Task.FromResult(Next);
        }
    }

    /// <summary>Dziennik, który tylko zapamiętuje — żeby dało się sprawdzić, co by zapisał.</summary>
    private sealed class Notes : IActivityLog
    {
        public List<(string Operation, string Outcome, ActivityLevel Level)> Wpisy { get; } = [];

        public int Dropped => 0;

        public Task RecordAsync(
            string operation, string outcome, ActivityLevel level = ActivityLevel.Ok,
            string? detail = null, CancellationToken ct = default)
        {
            Wpisy.Add((operation, outcome, level));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEntry>> RecentAsync(
            int count = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActivityEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();
    private readonly HlcSource _hlc;
    private readonly CalendarStore _sklad;
    private readonly Kanal _kanal = new(CalendarKind.Ical);
    private readonly CalendarSyncService _usluga;
    private readonly CalendarSource _zrodlo;
    private readonly TaskEditService _edycja;

    public CalendarStoreTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_zegar, "biurko");
        _sklad = new CalendarStore(_db);

        _usluga = new CalendarSyncService(
            _sklad, new TaskRepository(_db), [_kanal], _zegar, _hlc);

        _edycja = new TaskEditService(
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _zegar);

        _zrodlo = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(_zrodlo);
        _db.SaveChanges();
    }

    private static FeedEvent Wydarzenie(string id, string tytul, string dzien, int od, int doGodz) =>
        new(id, tytul,
            new DateTimeOffset(DateOnly.Parse(dzien).ToDateTime(new TimeOnly(od, 0)), TimeSpan.FromHours(2)),
            new DateTimeOffset(DateOnly.Parse(dzien).ToDateTime(new TimeOnly(doGodz, 0)), TimeSpan.FromHours(2)),
            false, null, false);

    [Fact]
    public async Task Pierwsze_pobranie_zapisuje_wydarzenia()
    {
        _kanal.Next = new FeedResult([Wydarzenie("a", "Zebranie", "2026-09-17", 17, 18)], null, true);

        var raport = await _usluga.RefreshAsync();

        raport.Sources.Should().Be(1);
        raport.Events.Should().Be(1);
        _db.CalendarEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Ponowne_pobranie_aktualizuje_zamiast_dublowac()
    {
        // Klucz to para źródło–identyfikator zewnętrzny, więc to samo wydarzenie
        // przychodzące drugi raz ma się zaktualizować, a nie dopisać.
        _kanal.Next = new FeedResult([Wydarzenie("a", "Zebranie", "2026-09-17", 17, 18)], null, true);
        await _usluga.RefreshAsync();

        _kanal.Next = new FeedResult([Wydarzenie("a", "Zebranie przełożone", "2026-09-17", 18, 19)], null, true);
        await _usluga.RefreshAsync(force: true);

        _db.CalendarEvents.Should().ContainSingle();
        _db.CalendarEvents.Single().Title.Should().Be("Zebranie przełożone");
    }

    [Fact]
    public async Task Pelne_pobranie_oznacza_zniknione_jako_odwolane()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("a", "Zostaje", "2026-09-17", 17, 18),
             Wydarzenie("b", "Znika", "2026-09-18", 17, 18)], null, true);
        await _usluga.RefreshAsync();

        _kanal.Next = new FeedResult([Wydarzenie("a", "Zostaje", "2026-09-17", 17, 18)], null, true);
        await _usluga.RefreshAsync(force: true);

        // Nagrobek, nie usunięcie — tak samo jak wszędzie indziej w tym modelu.
        _db.CalendarEvents.Should().HaveCount(2);
        _db.CalendarEvents.Single(e => e.ExternalId == "b").Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Pobranie_przyrostowe_nie_sprzata()
    {
        // Przy przyrostowym „nie przyszło" znaczy „bez zmian". To samo sprzątanie
        // skasowałoby cały kalendarz przy pierwszym pobraniu, w którym nic się nie zmieniło.
        _kanal.Next = new FeedResult([Wydarzenie("a", "Zostaje", "2026-09-17", 17, 18)], "zeton", true);
        await _usluga.RefreshAsync();

        _kanal.Next = new FeedResult([], "zeton2", IsFull: false);
        await _usluga.RefreshAsync(force: true);

        _db.CalendarEvents.Single().Cancelled.Should().BeFalse();
    }

    [Fact]
    public async Task Zeton_jest_zapamietywany_i_podawany_dalej()
    {
        _kanal.Next = new FeedResult([], "zeton-1", true);
        await _usluga.RefreshAsync();

        (await _sklad.CursorAsync(_zrodlo.Id))!.SyncToken.Should().Be("zeton-1");
    }

    [Fact]
    public async Task Swiezo_pobrany_kalendarz_nie_jest_pobierany_znowu()
    {
        await _usluga.RefreshAsync();
        await _usluga.RefreshAsync();

        _kanal.Wywolan.Should().Be(1);
    }

    [Fact]
    public async Task Po_godzinie_kalendarz_jest_pobierany_ponownie()
    {
        await _usluga.RefreshAsync();
        _zegar.Now = _zegar.Now.Add(CalendarSyncService.RefreshInterval).AddMinutes(1);
        await _usluga.RefreshAsync();

        _kanal.Wywolan.Should().Be(2);
    }

    [Fact]
    public async Task Niedostepny_kanal_nie_wywraca_odswiezania()
    {
        // Kalendarz jest dodatkiem do zadań. Niedostępny kanał ma znaczyć „brak świeżych
        // wydarzeń", a nie „aplikacja się nie otwiera".
        _kanal.Rzuca = true;

        var raport = await _usluga.RefreshAsync();

        raport.Failed.Should().Be(1);
        raport.Sources.Should().Be(0);
    }

    [Fact]
    public async Task Ukryty_kalendarz_znika_z_siatki_ale_zostaje_w_bazie()
    {
        _kanal.Next = new FeedResult([Wydarzenie("a", "Zebranie", "2026-09-16", 17, 18)], null, true);
        await _usluga.RefreshAsync();

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Should().ContainSingle();

        _zrodlo.SetVisible(false, _hlc.Next());
        _db.SaveChanges();

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Should().BeEmpty();
        _db.CalendarEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Odwolane_wydarzenie_nie_trafia_na_siatke()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("a", "Odwołane", "2026-09-16", 17, 18) with { Cancelled = true }], null, true);
        await _usluga.RefreshAsync();

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Should().BeEmpty();
    }

    [Fact]
    public async Task Zadanie_z_godzina_laduje_na_siatce_a_bez_godziny_na_pasku()
    {
        // Zgadywanie godziny zrobiłoby z listy zadań kalendarz, w którym wszystko jest
        // umówione — a to jest ten rodzaj planowania, który się nie utrzymuje.
        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zGodzina = TaskItem.Capture("Zadzwonić", _zegar.Now, _hlc.Next());
        zGodzina.Schedule(obszar.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        zGodzina.SetDoTime(new TimeOnly(14, 0), _hlc.Next());

        var bezGodziny = TaskItem.Capture("Kupić mleko", _zegar.Now, _hlc.Next());
        bezGodziny.Schedule(obszar.Id, new DateOnly(2026, 9, 16), _hlc.Next());

        _db.Tasks.AddRange(zGodzina, bezGodziny);
        _db.SaveChanges();

        var dzien = (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0];

        dzien.Timed.Should().ContainSingle();
        dzien.Timed[0].Entry.Title.Should().Be("Zadzwonić");
        dzien.Timed[0].Entry.StartHour.Should().Be(14);
        dzien.AllDay.Select(e => e.Title).Should().Equal("Kupić mleko");
    }

    [Fact]
    public async Task Zadanie_bez_dnia_wykonania_nie_trafia_na_siatke()
    {
        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Kiedyś", _zegar.Now, _hlc.Next());
        zadanie.MakeNext(obszar.Id, _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        var dzien = (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0];

        dzien.Timed.Should().BeEmpty();
        dzien.AllDay.Should().BeEmpty();
    }

    [Fact]
    public async Task Wydarzenie_trwajace_przez_dzisiaj_pokazuje_sie_mimo_ze_zaczelo_sie_wczoraj()
    {
        // Warunek zachodzenia zakresów, nie „start w zakresie".
        _kanal.Next = new FeedResult(
            [new FeedEvent(
                "a", "Wyjazd",
                new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.FromHours(2)),
                new DateTimeOffset(2026, 9, 18, 20, 0, 0, TimeSpan.FromHours(2)),
                false, null, false)],
            null, true);
        await _usluga.RefreshAsync();

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Should().ContainSingle();
    }

    [Fact]
    public async Task Kalendarz_da_sie_podlaczyc_i_odlaczyc()
    {
        // Do dziś nie było **żadnej** drogi, żeby dodać źródło: odświeżanie przechodziło
        // po kalendarzach, których nic nie umiało utworzyć, i kończyło się po cichu.
        var dodany = await _usluga.AddAsync(
            CalendarKind.Ical, "https://example.test/drugi.ics", "Zajęcia");

        (await _usluga.SourcesAsync()).Should().Contain(z => z.Id == dodany.Id);

        await _usluga.RemoveAsync(dodany.Id);

        (await _usluga.SourcesAsync()).Should().NotContain(z => z.Id == dodany.Id);

        // Nagrobek, nie usunięcie — wybór kalendarzy się synchronizuje (spec 5.1).
        _db.CalendarSources.Count(z => z.Id == dodany.Id).Should().Be(1);
    }

    [Fact]
    public async Task Szerokosc_kolumny_idzie_za_oknem()
    {
        // Stała szerokość na widok zostawiała dwie trzecie pustego miejsca obok siatki
        // na monitorze i kazała przewijać w bok w wąskim oknie. Bloki liczą się od
        // szerokości kolumny, więc muszą się przeliczyć razem z nią.
        _kanal.Next = new FeedResult(
            [Wydarzenie("s1", "Spotkanie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);

        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        model.SetAvailableWidth(900);

        model.ColumnWidth.Should().Be(300, "trzy dni z dziewięciuset punktów");
        model.Columns.SelectMany(k => k.Slots).Should().OnlyContain(b => b.Width <= 300);

        // Dolna granica: siedem kolumn po czternaście punktów to nie jest tydzień,
        // tylko siedem nieczytelnych pasków. Węższe okno ma się przewijać w bok.
        model.SetAvailableWidth(100);
        model.ColumnWidth.Should().BeGreaterThanOrEqualTo(96);
    }

    [Fact]
    public async Task Kreska_teraz_stoi_tylko_na_dzisiejszej_kolumnie()
    {
        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        var dzisiejsze = model.Columns.Where(k => k.IsToday).ToList();

        dzisiejsze.Should().ContainSingle("dzisiaj jest jedno");
        dzisiejsze[0].Date.Should().Be(_zegar.Today);

        // Zegar testu stoi na 9:00, godzina ma 48 punktów.
        dzisiejsze[0].NowTop.Should().Be(9 * 48);
    }

    [Fact]
    public async Task Odhaczenie_z_siatki_zamyka_zadanie()
    {
        // Odhaczenie ma iść tą samą drogą co z ekranu szczegółu — przez usługę edycji,
        // a nie przez wyrzucenie bloku z siatki. Blok znikający bez zapisu wyglądałby
        // identycznie i wracałby przy następnym odświeżeniu.
        var zadanie = TaskItem.Capture("Zadzwonić", _zegar.Now, _hlc.Next());
        zadanie.Schedule(Guid.CreateVersion7(), _zegar.Today, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        _db.Tasks.Add(zadanie);
        await _db.SaveChangesAsync();

        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        var blok = model.Columns.SelectMany(k => k.Slots).Single(b => b.Title == "Zadzwonić");
        blok.CanComplete.Should().BeTrue("zadanie da się odhaczyć, cudze wydarzenie nie");

        await model.CompleteCommand.ExecuteAsync(blok.TaskId);

        _db.Tasks.Single(z => z.Id == zadanie.Id).State.Should().Be(TaskState.Done);
    }

    [Fact]
    public async Task Siatka_otwiera_sie_na_biezacej_godzinie()
    {
        // Doba ma 1152 punkty, a ekran telefonu mieści z tego jakąś jedną czwartą:
        // otwarcie o północy pokazuje godziny, w których się śpi, i za każdym razem
        // zaczyna się od przewijania. Godzina zapasu u góry, stąd nie 9 * 48, a 8 * 48.
        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);

        double? dokad = null;
        model.ScrollRequested += punkty => dokad = punkty;

        await model.LoadAsync();

        dokad.Should().Be(8 * 48);
    }

    [Fact]
    public async Task Dni_bez_dzisiaj_nie_sa_przewijane()
    {
        // Godzina z innego dnia nie jest odpowiedzią na nic, a skok kasowałby
        // pozycję, którą użytkownik ustawił ręką przed chwilą.
        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);

        await model.LoadAsync();

        double? dokad = null;
        model.ScrollRequested += punkty => dokad = punkty;

        await model.NextCommand.ExecuteAsync(null);
        await model.LoadAsync();

        dokad.Should().BeNull();
    }

    [Fact]
    public async Task Pusta_siatka_przy_pelnej_bazie_trafia_do_dziennika()
    {
        // Ten dokładnie przypadek zżarł jedną rundę: 949 wydarzeń w bazie, osiem na
        // siatce, zero narysowanych — i trzy różne możliwe przyczyny wyglądające
        // identycznie. Wpis zapisuje się **tylko** wtedy, bo przy każdym przerysowaniu
        // zalałby dziennik tym, co i tak widać na ekranie.
        _kanal.Next = new FeedResult(
            [Wydarzenie("d1", "Dawno temu", "2026-01-05", 10, 11)], SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);

        var notes = new Notes();
        var model = new CalendarViewModel(_usluga, _zegar, notes, _edycja);

        await model.LoadAsync();

        notes.Wpisy.Should().Contain(w =>
            w.Operation == "Kalendarz: siatka" && w.Level == ActivityLevel.Problem);
    }

    [Fact]
    public async Task Barwa_dociaga_sie_przy_pobraniu()
    {
        // Kalendarze podłączone przed wprowadzeniem barw mają w bazie pusto i bez
        // tego zostałyby szare na zawsze — a jedyną drogą byłoby odłączenie ich
        // i dodanie od nowa, czyli naprawianie ręką czegoś, co aplikacja wie.
        _kanal.Next = new FeedResult([], SyncToken: null, IsFull: true, Color: "#8E24AA");

        await _usluga.RefreshAsync(force: true);

        (await _usluga.SourcesAsync())
            .Single(z => z.Id == _zrodlo.Id).Color.Should().Be("#8E24AA");
    }

    [Fact]
    public async Task Brak_barwy_u_zrodla_nie_kasuje_zapisanej()
    {
        // „Nie wiem" i „bez koloru" to dwie różne odpowiedzi. Kanał, który barwy nie
        // podaje, nie ma podstaw, żeby czyścić tę, którą już mamy.
        _kanal.Next = new FeedResult([], SyncToken: null, IsFull: true, Color: "#8E24AA");
        await _usluga.RefreshAsync(force: true);

        _kanal.Next = new FeedResult([], SyncToken: null, IsFull: true);
        await _usluga.RefreshAsync(force: true);

        (await _usluga.SourcesAsync())
            .Single(z => z.Id == _zrodlo.Id).Color.Should().Be("#8E24AA");
    }

    [Fact]
    public async Task Wydarzenie_dostaje_barwe_swojego_kalendarza()
    {
        // Barwa siedzi na kalendarzu, a rysuje się wydarzenie — i to jest jedyna
        // rzecz, po której przy jedenastu podłączonych kalendarzach widać, do
        // którego z nich coś należy. Droga wiedzie przez cztery warstwy, więc
        // urwana po cichu w dowolnej z nich wygląda jak „wszystko jest szare".
        var kolorowy = await _usluga.AddAsync(
            CalendarKind.Ical, "https://example.test/praca.ics", "Praca", "#8E24AA");

        await _sklad.UpsertAsync(
            kolorowy.Id, [Wydarzenie("p1", "Spotkanie", "2026-09-16", 10, 11)]);
        await _sklad.SaveChangesAsync();

        var dni = await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1);

        dni[0].Timed.Should().ContainSingle(s => s.Entry.Title == "Spotkanie")
            .Which.Entry.Color.Should().Be("#8E24AA");
    }

    [Fact]
    public async Task Ten_sam_kalendarz_dodany_dwa_razy_zostaje_jednym()
    {
        // Klikanie „Dodaj" w reakcji na to, że nic się nie pojawiło, jest odruchem —
        // a duplikaty mnożą potem te same błędy w raporcie i zaciemniają ten jeden,
        // który coś znaczy.
        var pierwszy = await _usluga.AddAsync(
            CalendarKind.Google, "primary", "Mój kalendarz");

        var drugi = await _usluga.AddAsync(
            CalendarKind.Google, "primary", "Mój kalendarz jeszcze raz");

        drugi.Id.Should().Be(pierwszy.Id);
        (await _usluga.SourcesAsync()).Count(z => z.ExternalId == "primary").Should().Be(1);
    }

    [Fact]
    public async Task Nieudany_kanal_mowi_dlaczego_a_nie_tylko_ze()
    {
        // Sama liczba porażek wygląda tak samo przy braku zgody, złym adresie i padniętej
        // sieci. Powód jest jedyną rzeczą, z której da się coś zrobić.
        _kanal.Rzuca = true;

        var raport = await _usluga.RefreshAsync(force: true);

        raport.Failed.Should().Be(1);
        raport.Problems.Should().ContainSingle()
            .Which.Should().Contain("Przedszkole").And.Contain("kanał nie odpowiada");
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}
