using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;
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

    /// <summary>Ustawienia z jedną strefą. Warszawa, bo o nią w tym projekcie chodzi.</summary>
    private sealed class Ustawienia : ISettings
    {
        public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

        public string? ZoneProblem => null;

        public ThemeChoice Theme => ThemeChoice.System;

        public string? GoogleClientId => null;

        public string? GoogleClientSecret => null;

        public bool GoogleCalendarEnabled => false;

        public void SetGoogleCalendarEnabled(bool enabled) => throw new NotSupportedException();

        public void SetZone(string id) => throw new NotSupportedException();

        public void SetTheme(ThemeChoice theme) => throw new NotSupportedException();

        public void SetGoogle(string? clientId, string? clientSecret) =>
            throw new NotSupportedException();
    }

    /// <summary>Pisarz, który tylko zapamiętuje, co by wysłał.</summary>
    private sealed class Pisarz : ICalendarWriter
    {
        public List<(string Co, string Tytul, string? Id)> Wyslane { get; } = [];

        public bool Rzuca { get; set; }

        public CalendarKind Kind => CalendarKind.Ical;

        public Task<string> CreateAsync(
            CalendarSource source, CalendarDraft draft, CancellationToken ct = default)
        {
            if (Rzuca)
            {
                throw new HttpRequestException("kalendarz nie odpowiada");
            }

            Wyslane.Add(("utworzenie", draft.Title, null));
            return Task.FromResult("nowe-1");
        }

        public Task UpdateAsync(
            CalendarSource source, string externalId, CalendarDraft draft,
            CancellationToken ct = default)
        {
            if (Rzuca)
            {
                throw new HttpRequestException("kalendarz nie odpowiada");
            }

            Wyslane.Add(("zmiana", draft.Title, externalId));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            CalendarSource source, string externalId, CancellationToken ct = default)
        {
            Wyslane.Add(("skasowanie", string.Empty, externalId));
            return Task.CompletedTask;
        }
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();
    private readonly HlcSource _hlc;
    private readonly CalendarStore _sklad;
    private readonly Kanal _kanal = new(CalendarKind.Ical);
    private readonly CalendarSyncService _usluga;
    private readonly CalendarSource _zrodlo;
    private readonly Pisarz _pisarz = new();
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
            _sklad, new TaskRepository(_db), [_kanal], _zegar, _hlc, new Ustawienia(), [_pisarz],
            new ProjectRepository(_db), new AreaRepository(_db));

        _edycja = new TaskEditService(
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _zegar, new AreaRepository(_db));

        _zrodlo = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(_zrodlo);
        _db.SaveChanges();
    }

    /// <summary>
    /// Dzisiaj według zegara testu.
    /// </summary>
    /// <remarks>
    /// Przez interfejs, nie przez atrapę: <c>Today</c> jest domyślną składową
    /// <see cref="IClock"/>, a takich nie widać przez typ, który interfejs realizuje.
    /// Drugi raz w tym projekcie.
    /// </remarks>
    private DateOnly Dzis => ((IClock)_zegar).Today;

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

    /// <summary>
    /// Zadanie bierze barwę obszaru, a gdy ma projekt z własną — barwę projektu.
    /// </summary>
    /// <remarks>
    /// Sens jest taki, że barwę ustawia się raz na obszarze, a nie przy każdym zadaniu.
    /// Kolorowanie po jednym zadaniu przestaje cokolwiek znaczyć po pierwszym tygodniu,
    /// bo barwa opisuje wtedy nastrój przy wpisywaniu, a nie przynależność.
    /// </remarks>
    [Fact]
    public async Task Zadanie_dziedziczy_barwe_obszaru_i_projektu()
    {
        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        obszar.SetColor("#3FA36B", _hlc.Next());
        _db.Areas.Add(obszar);

        var projekt = new Project(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Opony na aucie", obszar.Id, 0);
        projekt.SetColor("#CF5757", _hlc.Next());
        _db.Projects.Add(projekt);

        var zObszaru = TaskItem.Capture("Z obszaru", _zegar.Now, _hlc.Next());
        zObszaru.Schedule(obszar.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        zObszaru.SetDoTime(new TimeOnly(9, 0), _hlc.Next());

        var zProjektu = TaskItem.Capture("Z projektu", _zegar.Now, _hlc.Next());
        zProjektu.Schedule(obszar.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        zProjektu.SetDoTime(new TimeOnly(11, 0), _hlc.Next());
        zProjektu.MoveTo(obszar.Id, projekt.Id, _hlc.Next());

        _db.Tasks.AddRange(zObszaru, zProjektu);
        _db.SaveChanges();

        var dzien = (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0];

        dzien.Timed.Single(s => s.Entry.Title == "Z obszaru").Entry.Color.Should().Be("#3FA36B");
        dzien.Timed.Single(s => s.Entry.Title == "Z projektu").Entry.Color.Should().Be("#CF5757");
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
    public async Task Nowe_wydarzenie_idzie_najpierw_do_zrodla_potem_do_bazy()
    {
        var start = new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.FromHours(2));

        var id = await _usluga.SaveEventAsync(
            _zrodlo.Id, externalId: null,
            new CalendarDraft("Dentysta", start, start.AddHours(1)));

        id.Should().Be("nowe-1");
        _pisarz.Wyslane.Should().ContainSingle(w => w.Co == "utworzenie" && w.Tytul == "Dentysta");

        var dni = await _usluga.AgendaAsync(new DateOnly(2026, 9, 17), 1);
        dni[0].Timed.Should().ContainSingle(s => s.Entry.Title == "Dentysta");
    }

    [Fact]
    public async Task Nieudany_zapis_u_zrodla_nie_zostawia_wydarzenia_u_nas()
    {
        // Kolejność jest tu treścią, nie szczegółem. Gdyby baza szła pierwsza,
        // nieudany zapis zostawiłby wydarzenie widoczne w Marshalu, a nieistniejące
        // w kalendarzu — czyli dokładnie to, przed czym zapis dwustronny ma chronić.
        _pisarz.Rzuca = true;
        var start = new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.FromHours(2));

        var zapis = async () => await _usluga.SaveEventAsync(
            _zrodlo.Id, externalId: null,
            new CalendarDraft("Nie zapisze się", start, start.AddHours(1)));

        await zapis.Should().ThrowAsync<HttpRequestException>();

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 17), 1))[0]
            .Timed.Should().BeEmpty();
    }

    [Fact]
    public async Task Kalendarz_bez_pisarza_mowi_ze_jest_do_odczytu()
    {
        var bezPisarza = new CalendarSyncService(
            _sklad, new TaskRepository(_db), [_kanal], _zegar, _hlc, new Ustawienia(), [],
            new ProjectRepository(_db), new AreaRepository(_db));

        var start = new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.FromHours(2));

        var zapis = async () => await bezPisarza.SaveEventAsync(
            _zrodlo.Id, null, new CalendarDraft("Cokolwiek", start, start.AddHours(1)));

        (await zapis.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*tylko do odczytu*");
    }

    [Fact]
    public async Task Przeniesione_wydarzenie_da_sie_cofnac()
    {
        // Przeciągnięcie cudzego wydarzenia zapisuje się natychmiast w kalendarzu,
        // z którego pochodzi — bez pytania. „Cofnij" nie jest tu wygodą, tylko jedyną
        // odpowiedzią na omsknięcie ręki, więc ma odtwarzać **poprzednie** godziny.
        var start = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.FromHours(2));

        await _sklad.UpsertAsync(_zrodlo.Id,
            [new FeedEvent("w1", "Zebranie", start, start.AddHours(1), false, null, false)]);
        await _sklad.SaveChangesAsync();

        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        var blok = model.Columns.SelectMany(k => k.Slots).Single(b => b.Title == "Zebranie");

        await model.MoveEventAsync(blok, new DateOnly(2026, 9, 18), 14 * 48);

        _pisarz.Wyslane.Should().Contain(w => w.Co == "zmiana" && w.Id == "w1");
        model.CanUndo.Should().BeTrue();

        await model.UndoMoveCommand.ExecuteAsync(null);

        model.CanUndo.Should().BeFalse();

        // Dwie zmiany u źródła: przeniesienie i powrót. Ostatnia przywraca stan sprzed.
        var wydarzenie = _db.CalendarEvents.Single(e => e.ExternalId == "w1");

        wydarzenie.StartsAt.Should().Be(start);
        wydarzenie.EndsAt.Should().Be(start.AddHours(1));
    }

    [Fact]
    public async Task Odhaczenie_zapisuje_blok_konczacy_sie_teraz()
    {
        // Zadanie bez godziny znikało po odhaczeniu z kalendarza bez śladu, więc
        // wieczorem nie było z czego odczytać, na co poszedł dzień. Blok zapisuje się
        // wstecz: kończy się teraz, zaczyna tyle wcześniej, ile miało trwać.
        _zegar.Now = new DateTimeOffset(2026, 9, 17, 14, 37, 0, TimeSpan.FromHours(2));

        var zadanie = TaskItem.Capture("Zadzwonić", _zegar.Now, _hlc.Next());
        zadanie.Schedule(Guid.CreateVersion7(), Dzis, _hlc.Next());
        zadanie.SetEstimate(15, Energy.Unknown, _hlc.Next());
        _db.Tasks.Add(zadanie);
        await _db.SaveChangesAsync();

        await _edycja.CompleteAsync(zadanie.Id);

        var po = _db.Tasks.Single(z => z.Id == zadanie.Id);

        // 14:37 w dół do pięciu minut to 14:35, minus kwadrans daje 14:20.
        po.DoTime.Should().Be(new TimeOnly(14, 20));
        po.State.Should().Be(TaskState.Done);
    }

    [Fact]
    public async Task Odhaczenie_nie_rusza_godziny_wpisanej_wczesniej()
    {
        // Godzina wpisana wcześniej była decyzją, a nie zapisem tego, co się stało.
        _zegar.Now = new DateTimeOffset(2026, 9, 17, 14, 37, 0, TimeSpan.FromHours(2));

        var zadanie = TaskItem.Capture("Spotkanie", _zegar.Now, _hlc.Next());
        zadanie.Schedule(Guid.CreateVersion7(), Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(9, 0), _hlc.Next());
        _db.Tasks.Add(zadanie);
        await _db.SaveChangesAsync();

        await _edycja.CompleteAsync(zadanie.Id);

        _db.Tasks.Single(z => z.Id == zadanie.Id).DoTime.Should().Be(new TimeOnly(9, 0));
    }

    [Fact]
    public async Task Przeciagniecie_zmienia_dzien_i_godzine_a_nie_dlugosc()
    {
        var zadanie = TaskItem.Capture("Przesunąć", _zegar.Now, _hlc.Next());
        zadanie.Schedule(Guid.CreateVersion7(), Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(9, 0), _hlc.Next());
        zadanie.SetEstimate(90, Energy.Unknown, _hlc.Next());
        _db.Tasks.Add(zadanie);
        await _db.SaveChangesAsync();

        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        // Jutro, czternasta — czyli 14 * 48 punktów od góry siatki.
        await model.MoveAsync(zadanie.Id, Dzis.AddDays(1), 14 * 48);

        var po = _db.Tasks.Single(z => z.Id == zadanie.Id);

        po.DoDate.Should().Be(Dzis.AddDays(1));
        po.DoTime.Should().Be(new TimeOnly(14, 0));
        po.EstimatedMinutes.Should().Be(90, "przeciągnięcie przesuwa, a nie skraca");
    }

    [Fact]
    public void Klikniecie_w_puste_miejsce_zaokragla_godzine_do_piatki_minut()
    {
        // Pięć minut, nie kwadrans: kwadrans był za grubą miarką na spotkanie o 9:35.
        // Minuta co do punktu byłaby za to udawaną precyzją — trafienie w 14:07 nie
        // znaczy, że ktoś planuje na 14:07.
        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);

        (DateOnly Dzien, TimeOnly Pora)? poproszono = null;
        model.NewTaskRequested += (dzien, pora) => poproszono = (dzien, pora);

        // Czternasta i siedem minut w punktach: 14 * 48 plus 5,6.
        model.NewAt(new DateOnly(2026, 9, 17), (14 * 48) + 5.6);

        poproszono.Should().NotBeNull();
        poproszono!.Value.Dzien.Should().Be(new DateOnly(2026, 9, 17));
        poproszono.Value.Pora.Should().Be(new TimeOnly(14, 5));

        model.NewAt(new DateOnly(2026, 9, 17), (14 * 48) + 24);
        poproszono!.Value.Pora.Should().Be(new TimeOnly(14, 30));

        // Koniec doby nie przekręca się na następny dzień.
        model.NewAt(new DateOnly(2026, 9, 17), 24 * 48);
        poproszono!.Value.Pora.Should().Be(new TimeOnly(23, 55));
    }

    [Fact]
    public void Klikniete_wydarzenie_mowi_czym_jest_zamiast_milczec()
    {
        // Wydarzenia z cudzego kalendarza nie da się tu zmienić i to jest zamierzone.
        // Ale przycisk, który po kliknięciu nie robi nic, wygląda jak zepsuty — a nie
        // jak granica, która ma powód.
        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);

        var wydarzenie = new SlotBox(
            "Zebranie", 0, 48, 0, 200, IsTask: false, Color: null,
            "10:00", "11:00", TaskId: null, "17.09.2026",
            SourceId: null, ExternalId: null, IsDone: false);

        model.OpenTaskCommand.Execute(wydarzenie);

        model.HasOpened.Should().BeTrue();
        model.Opened!.Title.Should().Be("Zebranie");

        model.CloseOpenedCommand.Execute(null);
        model.HasOpened.Should().BeFalse();
    }

    [Fact]
    public async Task Calodniowe_stoi_na_jednym_dniu_a_nie_na_dwoch()
    {
        // Całodniowe to data, nie chwila. Google oddaje ją jako północ bez strefy;
        // przeliczenie na Warszawę robiło z niej drugą w nocy, więc koniec wypadał
        // drugiej w nocy **następnego** dnia i wpis rozlewał się na dwa dni.
        // Pełnia widoczna w Google na piątek stała u nas na piątku i sobocie.
        var dzien = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

        await _sklad.UpsertAsync(_zrodlo.Id,
        [
            new FeedEvent("ksiezyc", "Pierwsza kwadra", dzien, dzien.AddDays(1),
                IsAllDay: true, null, false),
        ]);
        await _sklad.SaveChangesAsync();

        var dni = await _usluga.AgendaAsync(new DateOnly(2026, 9, 18), 2);

        dni[0].AllDay.Should().ContainSingle(e => e.Title == "Pierwsza kwadra");
        dni[1].AllDay.Should().BeEmpty("to jest jeden dzień, a nie dwa");
    }

    [Fact]
    public async Task Godziny_licza_sie_ze_strefy_wydarzenia_a_nie_z_dzisiejszej()
    {
        // Sedno usterki: przesunięcie brane było **na teraz** i kładzione na każde
        // wydarzenie. Polska ma +2 latem i +1 zimą, więc oglądany w czerwcu grudzień
        // rysował się i podpisywał godzinę obok. Przesunięcie jest cechą chwili,
        // nie kalendarza.
        //
        // Południe UTC to 14:00 w Warszawie w lipcu i 13:00 w grudniu.
        var lato = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var zima = new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero);

        await _sklad.UpsertAsync(_zrodlo.Id,
        [
            new FeedEvent("lato", "Lipiec", lato, lato.AddHours(1), false, null, false),
            new FeedEvent("zima", "Grudzień", zima, zima.AddHours(1), false, null, false),
        ]);
        await _sklad.SaveChangesAsync();

        var wLipcu = await _usluga.AgendaAsync(new DateOnly(2026, 7, 15), 1);
        var wGrudniu = await _usluga.AgendaAsync(new DateOnly(2026, 12, 15), 1);

        wLipcu[0].Timed.Single().Entry.Start.Hour.Should().Be(14, "w lipcu Polska ma +2");
        wGrudniu[0].Timed.Single().Entry.Start.Hour.Should().Be(13, "w grudniu +1");
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

        // Jawnie trzy dni: widokiem domyślnym jest tydzień, a ten test mówi o dzieleniu
        // szerokości, nie o tym, ile dni pokazujemy na starcie.
        await model.ShowThreeDaysCommand.ExecuteAsync(null);
        model.SetAvailableWidth(900);

        // Dwieście dziewięćdziesiąt osiem, nie trzysta: kolumny dzieli odstęp i on też
        // zajmuje miejsce. Liczony wcześniej poza szerokością kolumny robił siatkę
        // o czternaście punktów szerszą niż okno — i stąd brał się poziomy pasek
        // przewijania przy oknie rozciągniętym na cały ekran.
        model.ColumnWidth.Should().Be(298, "trzy dni z dziewięciuset punktów bez odstępów");
        model.Columns.SelectMany(k => k.Slots).Should().OnlyContain(b => b.Width <= 298);

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
        dzisiejsze[0].Date.Should().Be(Dzis);

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
        zadanie.Schedule(Guid.CreateVersion7(), Dzis, _hlc.Next());
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
