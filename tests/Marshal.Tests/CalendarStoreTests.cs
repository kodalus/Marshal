using Avalonia.Media;
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

        public Guid? MainCalendarId { get; set; }

        public void SetMainCalendar(Guid? calendarId) => MainCalendarId = calendarId;

        public List<string> Konta { get; } = [];

        public IReadOnlyList<string> CalendarAccounts => Konta;

        public void AddCalendarAccount(string email) => Konta.Add(email);

        public void RemoveCalendarAccount(string email) => Konta.Remove(email);

        public void SetGoogleCalendarEnabled(bool enabled) => throw new NotSupportedException();

        public void SetZone(string id) => throw new NotSupportedException();

        public void SetTheme(ThemeChoice theme) => throw new NotSupportedException();

        public void SetGoogle(string? clientId, string? clientSecret) =>
            throw new NotSupportedException();
    }

    /// <summary>Pisarz, który tylko zapamiętuje, co by wysłał.</summary>
    private sealed class Pisarz : ICalendarWriter
    {
        public List<(string Co, string Tytul, string? Id, bool Calodniowe)> Wyslane { get; } = [];

        public bool Rzuca { get; set; }

        public CalendarKind Kind => CalendarKind.Ical;

        public Task<string> CreateAsync(
            CalendarSource source, CalendarDraft draft, CancellationToken ct = default)
        {
            if (Rzuca)
            {
                throw new HttpRequestException("kalendarz nie odpowiada");
            }

            Wyslane.Add(("utworzenie", draft.Title, null, draft.AllDay));
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

            Wyslane.Add(("zmiana", draft.Title, externalId, draft.AllDay));
            return Task.CompletedTask;
        }

        public Task RenameAsync(
            CalendarSource source, string externalId, string title,
            CancellationToken ct = default)
        {
            if (Rzuca)
            {
                throw new HttpRequestException("kalendarz nie odpowiada");
            }

            Wyslane.Add(("nazwa", title, externalId, false));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            CalendarSource source, string externalId, CancellationToken ct = default)
        {
            if (Rzuca)
            {
                throw new HttpRequestException("kalendarz nie odpowiada");
            }

            Wyslane.Add(("skasowanie", string.Empty, externalId, false));
            return Task.CompletedTask;
        }

        /// <summary>Kto już jest gościem którego wydarzenia — atrapa listy u źródła.</summary>
        public Dictionary<string, List<string>> Goscie { get; } = [];

        public Task<bool> InviteAsync(
            CalendarSource source, string externalId, string email, CancellationToken ct = default)
        {
            if (Rzuca)
            {
                throw new HttpRequestException("kalendarz nie odpowiada");
            }

            if (!Goscie.TryGetValue(externalId, out var lista))
            {
                lista = [];
                Goscie[externalId] = lista;
            }

            if (lista.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            lista.Add(email);
            Wyslane.Add(("zaproszenie", email, externalId, false));

            return Task.FromResult(true);
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
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _zegar, new AreaRepository(_db),
            new NoTaskMirror());

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
    public async Task Ten_sam_kalendarz_podlaczony_dwa_razy_rysuje_sie_raz()
    {
        // Tak to wygląda naprawdę: telefon podłącza swój kalendarz, a chwilę później
        // dochodzi do niego podłączenie z komputera — to samo, tylko z innym
        // identyfikatorem wiersza. Pilnowanie duplikatów siedziało wyłącznie na drodze
        // ręcznej, więc synchronizacja obchodziła je bokiem.
        // Minuta później, żeby „najstarsze" miało jednoznaczne znaczenie: dwa wiersze
        // założone w tej samej milisekundzie rozstrzyga dopiero identyfikator, a te są
        // wtedy względem siebie nieuporządkowane.
        _zegar.Now = _zegar.Now.AddMinutes(1);

        var zDrugiegoUrzadzenia = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(zDrugiegoUrzadzenia);
        _db.SaveChanges();

        _kanal.Next = new FeedResult([Wydarzenie("a", "Zebranie", "2026-09-17", 17, 18)], null, true);

        var raport = await _usluga.RefreshAsync(force: true);

        raport.Folded.Should().Be(1, "zostaje najstarsze podłączenie, reszta odpada");

        // Sedno: wydarzenie ma być jedno. Dwa źródła znaczyły dwa pobrania tego samego
        // kanału, a klucz kopii to para źródło–identyfikator — więc siatka rysowała
        // każde wydarzenie podwójnie, obok siebie, w tych samych barwach.
        _db.CalendarEvents.Should().ContainSingle();

        var zostale = await _sklad.SourcesAsync();
        zostale.Should().ContainSingle().Which.Id.Should().Be(_zrodlo.Id);
    }

    [Fact]
    public async Task Skladanie_duplikatow_przepina_to_co_wskazywalo_na_odrzucone()
    {
        // Bez przepięcia złożenie zostawiało wiszące wskazanie i objawiało się dopiero
        // przy zapisie: „tego kalendarza już nie ma na liście podłączonych" przy zadaniu,
        // którego nikt nie ruszał. Odczyt szedł przez to, co zostało, a zapis przez to,
        // co zniknęło.
        _zegar.Now = _zegar.Now.AddMinutes(1);

        var mlodsze = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(mlodsze);

        var zadanie = TaskItem.Capture("Zebranie", _zegar.Now, _hlc.Next());
        zadanie.Share(mlodsze.Id, "zewnetrzny-1", _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        var ustawienia = new Ustawienia();
        ustawienia.SetMainCalendar(mlodsze.Id);

        var usluga = new CalendarSyncService(
            _sklad, new TaskRepository(_db), [_kanal], _zegar, _hlc, ustawienia, [_pisarz],
            new ProjectRepository(_db), new AreaRepository(_db));

        _kanal.Next = new FeedResult([], null, true);

        (await usluga.RefreshAsync(force: true)).Folded.Should().Be(1);

        // Obie strony wskazywały **ten sam** kalendarz u źródła — na tym polega bycie
        // duplikatem — więc identyfikator wydarzenia zostaje ważny.
        ustawienia.MainCalendarId.Should().Be(_zrodlo.Id);

        var przepiete = _db.Tasks.Single(t => t.Id == zadanie.Id);
        przepiete.SharedCalendarId.Should().Be(_zrodlo.Id);
        przepiete.SharedEventId.Should().Be("zewnetrzny-1");
    }

    [Fact]
    public async Task Wskazanie_na_odrzucone_podlaczenie_jest_starym_imieniem_zyjacego()
    {
        // Ten sam kalendarz Google podłączony na dwóch urządzeniach ma na każdym własny
        // identyfikator wiersza. Wybór kalendarza się synchronizuje, więc zadanie
        // udostępnione na komputerze przyjeżdża na telefon ze wskazaniem na wiersz
        // komputera — a składanie duplikatów robi z jednego z nich nagrobek.
        _zegar.Now = _zegar.Now.AddMinutes(1);

        var zDrugiegoUrzadzenia = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(zDrugiegoUrzadzenia);
        _db.SaveChanges();

        _kanal.Next = new FeedResult([], null, true);
        (await _usluga.RefreshAsync(force: true)).Folded.Should().Be(1);

        // Sedno: wskazanie na nagrobek nie jest brakiem kalendarza. Odmowa przy nim
        // wyglądała absurdalnie — wszystkie kalendarze wyświetlały się poprawnie,
        // a zadania z drugiego urządzenia nie dawały się ani zapisać, ani skasować.
        (await _usluga.ZywyKalendarzAsync(zDrugiegoUrzadzenia.Id)).Should().Be(_zrodlo.Id);
        (await _usluga.ZywyKalendarzAsync(_zrodlo.Id)).Should().Be(_zrodlo.Id);
    }

    [Fact]
    public async Task Podlaczenie_ktorego_naprawde_nie_ma_zostaje_puste()
    {
        // Jedyny przypadek, w którym zapis ma odmówić: odłączono ręcznie i nic nie
        // zajęło miejsca. Bez tego rozróżnienia naprawa przykrywałaby prawdziwy brak.
        (await _usluga.ZywyKalendarzAsync(Guid.CreateVersion7())).Should().BeNull();
    }

    [Fact]
    public async Task Odswiezenie_naprawia_wiszacy_kalendarz_glowny()
    {
        // Składanie duplikatów przepina to, co widzi w chwili składania. Aplikacja,
        // w której złożenie odbyło się przed tą poprawką, zostałaby ze wskazaniem
        // wiszącym na zawsze — stąd naprawa przy każdym odświeżeniu.
        _zegar.Now = _zegar.Now.AddMinutes(1);

        var mlodsze = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        mlodsze.MarkDeleted(_hlc.Next());
        _db.CalendarSources.Add(mlodsze);
        _db.SaveChanges();

        var ustawienia = new Ustawienia { MainCalendarId = mlodsze.Id };

        var usluga = new CalendarSyncService(
            _sklad, new TaskRepository(_db), [_kanal], _zegar, _hlc, ustawienia, [_pisarz],
            new ProjectRepository(_db), new AreaRepository(_db));

        _kanal.Next = new FeedResult([], null, true);
        await usluga.RefreshAsync(force: true);

        ustawienia.MainCalendarId.Should().Be(_zrodlo.Id);
    }

    [Fact]
    public async Task Skladanie_duplikatow_kasuje_kopie_odrzuconego_zrodla()
    {
        // Odrzucone źródło zostawiało po sobie wydarzenia: niewidoczne, bo źródła już
        // nie ma, ale policzone w „ile w bazie" — czyli mylące dokładnie w tym miejscu,
        // w którym się patrzy, szukając duplikatów.
        _zegar.Now = _zegar.Now.AddMinutes(1);

        var drugie = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(drugie);
        _db.CalendarEvents.Add(new CalendarEvent(
            drugie.Id, "a", "Zebranie",
            new DateTimeOffset(2026, 9, 17, 17, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 17, 18, 0, 0, TimeSpan.FromHours(2)),
            false));
        _db.SaveChanges();

        _kanal.Next = new FeedResult([], null, true);

        await _usluga.RefreshAsync(force: true);

        _db.CalendarEvents.Should().BeEmpty();
        (await _sklad.CountAsync()).Should().Be(0);
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

    /// <summary>Po odstępie kalendarz pobiera się ponownie — sam, bez proszenia.</summary>
    /// <remarks>
    /// Odstęp liczony z nazwanej stałej, nie z wpisanej godziny: sama jego długość jest
    /// decyzją, która się zmienia, a to, że po jego upływie sięgamy po świeże dane,
    /// zmienić się nie ma.
    /// </remarks>
    [Fact]
    public async Task Po_odstepie_kalendarz_jest_pobierany_ponownie()
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

    /// <summary>
    /// Przeciągnięcie odhaczonego bloku przesuwa go, a nie wskrzesza.
    /// </summary>
    /// <remarks>
    /// Nadanie dnia szło przez przejście stanu (N8), więc ustawiało „zaplanowane"
    /// i tym samym zdejmowało „wykonane" — ptaszek znikał po każdym przeciągnięciu.
    /// Ruch po siatce poprawia zapis o przeszłości; cofnięcie decyzji ma być osobną,
    /// widoczną czynnością.
    /// </remarks>
    [Fact]
    public async Task Przeciagniecie_odhaczonego_zadania_zostawia_ptaszek()
    {
        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Zrobione", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        await _edycja.CompleteAsync(zadanie.Id);
        await _edycja.RescheduleAsync(zadanie.Id, Dzis, new TimeOnly(15, 30));

        var poPrzeciagnieciu = _db.Tasks.Single(t => t.Id == zadanie.Id);
        poPrzeciagnieciu.State.Should().Be(TaskState.Done);
        poPrzeciagnieciu.DoTime.Should().Be(new TimeOnly(15, 30));

        (await _usluga.AgendaAsync(Dzis, 1))[0].Timed
            .Single(b => b.Entry.TaskId == zadanie.Id).Entry.IsDone.Should().BeTrue();
    }

    /// <summary>
    /// Zdjęcie ptaszka przywraca zadanie do żywych — z powrotem do zaplanowanych.
    /// </summary>
    /// <remarks>
    /// Odhaczenie kosztuje jedno kliknięcie, więc omyłkowe zdarza się tak samo łatwo.
    /// Do dziś cofnąć dało się je wyłącznie przez bazę, mimo że model to umiał.
    /// </remarks>
    [Fact]
    public async Task Zdjecie_ptaszka_przywraca_zadanie_do_zaplanowanych()
    {
        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Pomyłka", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        await _edycja.CompleteAsync(zadanie.Id);
        await _edycja.ReopenAsync(zadanie.Id);

        var poZdjeciu = _db.Tasks.Single(t => t.Id == zadanie.Id);
        poZdjeciu.State.Should().Be(TaskState.Scheduled);
        poZdjeciu.CompletedAt.Should().BeNull();

        (await _usluga.AgendaAsync(Dzis, 1))[0].Timed
            .Single(b => b.Entry.TaskId == zadanie.Id).Entry.IsDone.Should().BeFalse();
    }

    /// <summary>
    /// Odhaczenie wydarzenia stawia ptaszek w jego nazwie u źródła.
    /// </summary>
    /// <remarks>
    /// Wydarzenie nie ma u nas pola „zrobione" i nie powinno mieć: własna kolumna
    /// znaczyłaby ptaszek widoczny wyłącznie w Marshalu. Znak w nazwie widać w Google,
    /// na telefonie i w powiadomieniu — i wraca do nas sam przy odświeżeniu.
    /// </remarks>
    [Fact]
    public async Task Odhaczenie_wydarzenia_stawia_ptaszek_w_nazwie_u_zrodla()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("s1", "Spotkanie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);

        await _usluga.SetEventDoneAsync(_zrodlo.Id, "s1", done: true);

        _pisarz.Wyslane.Should().ContainSingle()
            .Which.Should().Be(("nazwa", "✓ Spotkanie", "s1", false));

        var wpis = (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Single();
        wpis.Entry.IsDone.Should().BeTrue();

        // Sam ptaszek nie jest częścią nazwy: obok kwadracika stałby drugi raz,
        // a zmiana nazwy w oknie odesłałaby go do Google zapisanego podwójnie.
        wpis.Entry.Title.Should().Be("Spotkanie");
    }

    [Fact]
    public async Task Zdjecie_ptaszka_z_wydarzenia_wraca_do_czystej_nazwy()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("s1", "✓ Spotkanie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
            .Timed.Single().Entry.IsDone.Should().BeTrue();

        await _usluga.SetEventDoneAsync(_zrodlo.Id, "s1", done: false);

        _pisarz.Wyslane.Should().ContainSingle()
            .Which.Should().Be(("nazwa", "Spotkanie", "s1", false));

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
            .Timed.Single().Entry.IsDone.Should().BeFalse();
    }

    /// <summary>
    /// Nieudany zapis u źródła nie zostawia ptaszka u nas.
    /// </summary>
    /// <remarks>
    /// Najpierw źródło, potem nasza kopia. Odwrotna kolejność dawałaby ptaszek widoczny
    /// w Marshalu i nieistniejący nigdzie indziej — czyli dokładnie to kłamstwo,
    /// przed którym zapis dwustronny ma chronić.
    /// </remarks>
    [Fact]
    public async Task Nieudane_odhaczenie_wydarzenia_nie_zostawia_ptaszka_u_nas()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("s1", "Spotkanie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);
        _pisarz.Rzuca = true;

        var odhacz = async () => await _usluga.SetEventDoneAsync(_zrodlo.Id, "s1", done: true);

        await odhacz.Should().ThrowAsync<HttpRequestException>();

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
            .Timed.Single().Entry.IsDone.Should().BeFalse();
    }

    /// <summary>
    /// Rozciągnięcie bloku za dolną krawędź zmienia długość zadania, nie jego porę.
    /// </summary>
    /// <remarks>
    /// Długość jest w modelu oszacowaniem — koniec wynika z początku i długości, a nie
    /// z drugiej daty. Rozciąganie podaje więc tę samą liczbę, którą widać w szczegółach
    /// i po której dobiera „Teraz"; przeciągnięcie krawędzi jest tylko najszybszym
    /// sposobem, żeby ją wpisać.
    /// </remarks>
    [Fact]
    public async Task Rozciagniecie_bloku_zmienia_dlugosc_a_nie_pore()
    {
        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Spotkanie", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        zadanie.SetEstimate(30, Energy.Medium, _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        var blok = model.Columns.SelectMany(k => k.Slots).Single(b => b.TaskId == zadanie.Id);

        // Dolna krawędź ściągnięta na 11:20. Godzina zaczepienia zostaje, długość rośnie.
        await model.ResizeAsync(blok, (11 * 48) + (20 * 48 / 60.0));

        var po = _db.Tasks.Single(t => t.Id == zadanie.Id);
        po.DoTime.Should().Be(new TimeOnly(10, 0), "rozciąganie nie rusza pory zaczepienia");
        po.EstimatedMinutes.Should().Be(80);
        po.Energy.Should().Be(Energy.Medium, "zmieniamy jedno pole, nie dwa");
    }

    /// <summary>Pociągnięcie krawędzi ponad początek daje najkrótszy blok, nie ujemny.</summary>
    [Fact]
    public async Task Rozciagniecie_ponad_poczatek_daje_najkrotszy_blok()
    {
        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Spotkanie", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        zadanie.SetEstimate(60, Energy.Medium, _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        var blok = model.Columns.SelectMany(k => k.Slots).Single(b => b.TaskId == zadanie.Id);

        await model.ResizeAsync(blok, 8 * 48);

        _db.Tasks.Single(t => t.Id == zadanie.Id).EstimatedMinutes.Should().Be(5);
    }

    /// <summary>
    /// Udostępnione zadanie żyje w kalendarzu jako wydarzenie i nadąża za zmianami.
    /// </summary>
    /// <remarks>
    /// Po to, żeby ktoś bez Marshala widział u siebie to, co go dotyczy. Udostępnia się
    /// pojedyncze zadania, nie całe obszary: obszar rodzinny mieści i „odebrać dziecko",
    /// i „kupić prezent", a widzieć je mają różne osoby.
    /// </remarks>
    [Fact]
    public async Task Udostepnione_zadanie_ma_swoje_wydarzenie_i_nadaza_za_zmianami()
    {
        var odbicie = new TaskMirror(
            _usluga, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Ustawienia(), new Notes(),
            new AreaRepository(_db));

        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Odebrać Sanię", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(16, 0), _hlc.Next());
        zadanie.SetEstimate(30, Energy.Medium, _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        (await odbicie.ShareAsync(zadanie.Id, _zrodlo.Id)).Should().BeNull("miało się udać");

        _pisarz.Wyslane.Should().ContainSingle()
            .Which.Should().Be(("utworzenie", "Odebrać Sanię", (string?)null, false));

        _db.Tasks.Single(t => t.Id == zadanie.Id).SharedEventId.Should().Be("nowe-1");

        // Przesunięcie po siatce ma dojść do kalendarza, bo tam ktoś na to patrzy.
        var edycja = new TaskEditService(
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _zegar,
            new AreaRepository(_db), odbicie);

        await edycja.RescheduleAsync(zadanie.Id, Dzis, new TimeOnly(17, 30));

        _pisarz.Wyslane.Should().HaveCount(2);
        _pisarz.Wyslane[1].Co.Should().Be("zmiana");

        // Odhaczenie dokłada ptaszek do nazwy — druga osoba widzi, że zrobione.
        await edycja.CompleteAsync(zadanie.Id);
        _pisarz.Wyslane[^1].Tytul.Should().Be("✓ Odebrać Sanię");

        // I najważniejsze: na siatce stoi **jeden** blok, nie dwa. Zapis do kalendarza
        // wraca do naszej kopii jako wydarzenie; narysowane obok zadania dałoby dwa
        // bloki na tę samą rzecz, z których jeden nie dawałby się odhaczyć.
        (await _usluga.AgendaAsync(Dzis, 1))[0].Timed
            .Should().ContainSingle()
            .Which.Entry.TaskId.Should().Be(zadanie.Id);
    }

    /// <summary>
    /// Zadanie wyrzucone do kosza zabiera ze sobą swoje wydarzenie.
    /// </summary>
    /// <remarks>
    /// Zostawione w cudzym kalendarzu byłoby zaproszeniem na coś, co po tej stronie
    /// już nie istnieje — i nikt by go stamtąd nie zdjął, bo nie miałby po czym poznać.
    /// </remarks>
    [Fact]
    public async Task Kosz_zabiera_ze_soba_udostepnione_wydarzenie()
    {
        var odbicie = new TaskMirror(
            _usluga, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Ustawienia(), new Notes(),
            new AreaRepository(_db));

        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Odwołane", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(9, 0), _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        await odbicie.ShareAsync(zadanie.Id, _zrodlo.Id);

        var skrzynka = new InboxService(
            new TaskRepository(_db), new ProjectRepository(_db), new UnitOfWork(_db),
            _zegar, _hlc, odbicie);

        await skrzynka.TrashAsync(zadanie.Id);

        _pisarz.Wyslane[^1].Co.Should().Be("skasowanie");

        var po = _db.Tasks.Single(t => t.Id == zadanie.Id);
        po.SharedCalendarId.Should().BeNull();
        po.SharedEventId.Should().BeNull();
    }

    /// <summary>
    /// Kasowanie odbicia, które się nie udało, zostaje dokończone później.
    /// </summary>
    /// <remarks>
    /// Odbicie jest robione po kliknięciu i nie ma trwałości: zadanie wyrzucone przy
    /// padniętej sieci albo tuż przed zamknięciem aplikacji zostawiało w kalendarzu
    /// wydarzenie, po którym nikt już nie sprzątał.
    ///
    /// Na siatce tego dziś nie widać, bo wskazanie w zadaniu trwa i cień pozostaje
    /// cieniem — ale w cudzym kalendarzu wpis nadal stoi, więc zdjęcie go jest wciąż
    /// do zrobienia. Ten test pilnuje tego, co zostało po drugiej stronie, a nie tego,
    /// co widać po naszej.
    /// </remarks>
    [Fact]
    public async Task Nieudane_kasowanie_odbicia_zostaje_dokonczone()
    {
        var odbicie = new TaskMirror(
            _usluga, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Ustawienia(), new Notes(),
            new AreaRepository(_db));

        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Zostanie sierotą", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(11, 0), _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        await odbicie.ShareAsync(zadanie.Id, _zrodlo.Id);

        // Wyrzucenie bez odbicia — dokładnie to, co zostaje po odłożonym kasowaniu,
        // które nie doszło do skutku: zadanie w koszu, wskazanie na wydarzenie całe.
        var skrzynka = new InboxService(
            new TaskRepository(_db), new ProjectRepository(_db), new UnitOfWork(_db),
            _zegar, _hlc, new NoTaskMirror());

        await skrzynka.TrashAsync(zadanie.Id);

        // Z ekranu nie widać nic i tak ma być: wskazanie w zadaniu jeszcze jest, więc
        // wydarzenie nadal jest cieniem, a nie osobnym wpisem. Dopóki kasowanie się nie
        // powiodło, siatka czeka na skutek, zamiast pokazywać rzecz w połowie drogi.
        //
        // Ten test opisywał kiedyś objaw odwrotny — wpis stojący dalej, ale już nie jako
        // zadanie — i objaw ten był całą usterką, a nie umową.
        (await _usluga.AgendaAsync(Dzis, 1))[0].Timed
            .Should().BeEmpty("cień nie staje się osobnym wpisem, dopóki wskazanie trwa");

        // Pierwsze podejście pada — sieć nie odpowiada. Ma nie rzucić wyżej: dokańczanie
        // chodzi z minutnika i wywrotka zabrałaby ze sobą wszystko, co robi się obok.
        _pisarz.Rzuca = true;
        await odbicie.DokonczKasowaniaAsync();

        _db.Tasks.Single(t => t.Id == zadanie.Id).SharedEventId
            .Should().NotBeNull("nieudane kasowanie nie ma prawa zapomnieć wskazania");

        // Drugie dochodzi — i wtedy znika i u źródła, i u nas.
        _pisarz.Rzuca = false;
        await odbicie.DokonczKasowaniaAsync();

        _pisarz.Wyslane[^1].Co.Should().Be("skasowanie");

        var po = _db.Tasks.Single(t => t.Id == zadanie.Id);
        po.SharedCalendarId.Should().BeNull();
        po.SharedEventId.Should().BeNull();

        (await _usluga.AgendaAsync(Dzis, 1))[0].Timed.Should().BeEmpty();
    }

    /// <summary>Bez daty nie ma czego udostępnić — i mówimy to wprost.</summary>
    /// <remarks>
    /// Sama godzina nie jest już wymagana: zadanie z dniem, ale bez pory, idzie jako
    /// wydarzenie całodniowe. Bez **daty** kalendarz nadal nie ma gdzie go postawić.
    /// </remarks>
    [Fact]
    public async Task Zadanie_bez_daty_nie_da_sie_udostepnic()
    {
        var odbicie = new TaskMirror(
            _usluga, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Ustawienia(), new Notes(),
            new AreaRepository(_db));

        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Kiedyś, nie wiadomo kiedy", _zegar.Now, _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        (await odbicie.ShareAsync(zadanie.Id, _zrodlo.Id))
            .Should().Contain("Najpierw dzień");

        _pisarz.Wyslane.Should().BeEmpty("odmowa nie dotyka kalendarza");
    }

    /// <summary>
    /// Zadanie z godziną ląduje w kalendarzu głównym samo, bez proszenia.
    /// </summary>
    /// <remarks>
    /// Bez tego każde trzeba było przenosić ręcznie, jedno po drugim — a synchronizacja,
    /// o której trzeba pamiętać przy każdym zadaniu, nie jest synchronizacją.
    /// </remarks>
    [Fact]
    public async Task Zadanie_z_godzina_trafia_do_kalendarza_glownego_samo()
    {
        var ustawienia = new Ustawienia { MainCalendarId = _zrodlo.Id };
        var odbicie = new TaskMirror(
            _usluga, new TaskRepository(_db), new UnitOfWork(_db), _hlc, ustawienia, new Notes(),
            new AreaRepository(_db));

        var edycja = new TaskEditService(
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _zegar,
            new AreaRepository(_db), odbicie);

        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Odebrać Sanię", _zegar.Now, _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        // Sam dzień wystarczy — idzie jako wydarzenie całodniowe. Zgadnięta godzina
        // zrobiłaby z zadania spotkanie, którego nikt nie umawiał.
        await edycja.RescheduleAsync(zadanie.Id, Dzis, null);

        _pisarz.Wyslane.Should().ContainSingle()
            .Which.Should().Match<(string Co, string Tytul, string? Id, bool Calodniowe)>(
                w => w.Co == "utworzenie" && w.Calodniowe);

        // Dopisana godzina zmienia to samo wydarzenie, a nie zakłada drugiego.
        await edycja.RescheduleAsync(zadanie.Id, Dzis, new TimeOnly(16, 0));

        _pisarz.Wyslane.Should().HaveCount(2);
        _pisarz.Wyslane[^1].Co.Should().Be("zmiana");
        _pisarz.Wyslane[^1].Calodniowe.Should().BeFalse("od tej chwili ma swoją porę");
        _db.Tasks.Single(t => t.Id == zadanie.Id).SharedCalendarId.Should().Be(_zrodlo.Id);
    }

    /// <summary>
    /// Zadanie zdjęte z dnia nie zostawia na siatce swojego odbicia.
    /// </summary>
    /// <remarks>
    /// Zadanie znika z siatki natychmiast, a zdjęcie jego odbicia z Google idzie przez
    /// sieć i wraca sekundę albo dwie później. Przez tę chwilę cień nie był już przez
    /// nic odsiewany — odsiew szedł po zadaniach z oglądanego zakresu, a tego zadania
    /// w nim właśnie zabrakło — i rysował się jako całodniowy pasek przez cały dzień.
    /// </remarks>
    [Fact]
    public async Task Odbicie_zadania_zdjetego_z_dnia_nie_rysuje_sie_jako_wydarzenie()
    {
        var odbicie = new TaskMirror(
            _usluga, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Ustawienia(), new Notes(),
            new AreaRepository(_db));

        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Wzięte na dziś", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        await odbicie.ShareAsync(zadanie.Id, _zrodlo.Id);

        // Jeden blok: zadanie. Jego wydarzenie w Google jest cieniem i nie rysuje się.
        (await _usluga.AgendaAsync(Dzis, 1))[0].AllDay
            .Should().ContainSingle().Which.TaskId.Should().Be(zadanie.Id);

        // Zdjęcie z dnia — bez zdejmowania odbicia, czyli dokładnie stan z tej sekundy,
        // w której zadanie już zniknęło, a odpowiedź z sieci jeszcze nie wróciła.
        _db.Tasks.Single(t => t.Id == zadanie.Id).LeaveAsDebt(_hlc.Next());
        _db.SaveChanges();

        (await _usluga.AgendaAsync(Dzis, 1))[0].AllDay
            .Should().BeEmpty("cień nie ma prawa stać się osobnym wpisem");
    }

    /// <summary>
    /// Plan dnia — czyli widget — pokazuje także wydarzenia z podłączonych kalendarzy.
    /// </summary>
    /// <remarks>
    /// Bez nich widget odpowiadał na pytanie „co mam dziś w Marshalu", a nie „co mam
    /// dziś". Wizyta u lekarza wpisana w Google zajmuje dzień tak samo jak zadanie,
    /// a plan, który ją pomija, kłamie o tym, ile zostało czasu.
    /// </remarks>
    [Fact]
    public async Task Plan_dnia_pokazuje_wydarzenia_z_kalendarza()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("s1", "Lekarz", Dzis.ToString("yyyy-MM-dd"), 10, 11)],
            SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);

        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Kupić mleko", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(8, 0), _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        var plan = new PlanDniaService(
            new TaskRepository(_db), new ProjectRepository(_db), new AreaRepository(_db),
            _zegar, _usluga);

        var pozycje = await plan.DlaDniaAsync(Dzis);

        // Po godzinach, niezależnie od tego, skąd wpis pochodzi — dzień ma jedną oś.
        pozycje.Select(p => p.Tytul).Should().Equal("Kupić mleko", "Lekarz");

        // Wydarzenie bez identyfikatora zadania: na widgecie nie ma czego odhaczyć
        // jednym dotknięciem, bo ptaszek idzie do cudzego kalendarza przez sieć.
        pozycje.Single(p => p.Tytul == "Lekarz").Zadanie.Should().BeNull();
        pozycje.Single(p => p.Tytul == "Kupić mleko").Zadanie.Should().Be(zadanie.Id);
    }

    /// <summary>
    /// Pokazanie wydarzenia osobie dopisuje ją do gości, a powtórka nie dopisuje drugi raz.
    /// </summary>
    /// <remarks>
    /// Druga z dwóch dróg Google’a i jedyna działająca na pojedynczej rzeczy:
    /// udostępniony kalendarz znaczy „ta półka jest nasza wspólna", a gość — „spójrz
    /// na to jedno". Powtórne pokazanie tej samej osobie nie ma nic do zrobienia
    /// i ma to powiedzieć, zamiast wysyłać drugie zaproszenie.
    /// </remarks>
    [Fact]
    public async Task Pokazanie_osobie_dopisuje_ja_do_gosci()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("s1", "Wywiadówka", "2026-09-16", 17, 18)], SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);

        (await _usluga.InviteAsync(_zrodlo.Id, "s1", "sylw@example.test"))
            .Should().BeTrue("tej osoby jeszcze nie było na liście");

        _pisarz.Wyslane[^1].Should().Be(("zaproszenie", "sylw@example.test", "s1", false));

        (await _usluga.InviteAsync(_zrodlo.Id, "s1", "sylw@example.test"))
            .Should().BeFalse("już to widzi");

        _pisarz.Goscie["s1"].Should().ContainSingle();
    }

    /// <summary>
    /// Przełożenie wydarzenia do innego kalendarza zakłada w nowym, zanim skasuje w starym.
    /// </summary>
    /// <remarks>
    /// Kolejność odwrotna niż przy przenoszeniu zadania i to jest rozstrzygnięcie.
    /// Przy zadaniu prawdą jest zadanie, więc nieudane założenie da się powtórzyć
    /// z tego, co i tak mamy — tam idzie najpierw skasowanie, żeby nie zostały dwa
    /// wpisy. Tutaj prawdą jest wydarzenie, a nasza kopia jest kopią: nieudane
    /// założenie po skasowaniu znaczyłoby cudzy wpis skasowany bezpowrotnie.
    /// </remarks>
    [Fact]
    public async Task Przelozenie_wydarzenia_zaklada_przed_skasowaniem()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("s1", "Zebranie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);

        var rodzinny = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/rodzina.ics", "Rodzina");

        _db.CalendarSources.Add(rodzinny);
        _db.SaveChanges();

        var nowy = await _usluga.MoveEventAsync(_zrodlo.Id, "s1", rodzinny.Id);

        nowy.Should().NotBe("s1", "u Google to jest inne wydarzenie w innym kalendarzu");

        _pisarz.Wyslane.Select(w => w.Co).Should().Equal("utworzenie", "skasowanie");

        // U nas zostaje jeden wpis, w nowym kalendarzu.
        var naSiatce = (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed;

        naSiatce.Should().ContainSingle();
        naSiatce.Single().Entry.SourceId.Should().Be(rodzinny.Id);
    }

    /// <summary>
    /// Wydarzenie z przypisanego kalendarza ma barwę swojego obszaru.
    /// </summary>
    /// <remarks>
    /// Kalendarz przypisany do obszaru jest tym obszarem, więc wydarzenie stamtąd
    /// ma wyglądać jak wszystko inne z tej półki. Inaczej odbiór dziecka wpisany
    /// w Google i odbiór dziecka wpisany w Marshalu stałyby obok siebie w dwóch
    /// kolorach, choć są tą samą rzeczą.
    /// </remarks>
    [Fact]
    public async Task Wydarzenie_bierze_barwe_obszaru_swojego_kalendarza()
    {
        _kanal.Next = new FeedResult(
            [Wydarzenie("s1", "Zebranie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _usluga.RefreshAsync(force: true);

        var dzieci = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dzieci", 0);
        dzieci.SetColor("#FF8800", _hlc.Next());
        dzieci.SetCalendar(_zrodlo.Id, _hlc.Next());
        _db.Areas.Add(dzieci);
        _db.SaveChanges();

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
            .Timed.Single().Entry.Color.Should().Be("#FF8800");

        // Zdjęcie przypisania oddaje wydarzenie barwie samego kalendarza — tam wracają
        // święta i wywiadówki, czyli wszystko, czego nikt do obszaru nie przypisał.
        dzieci.SetCalendar(null, _hlc.Next());
        _db.SaveChanges();

        (await _usluga.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
            .Timed.Single().Entry.Color.Should().NotBe("#FF8800");
    }

    /// <summary>
    /// Kalendarz stoi przy jednym obszarze naraz.
    /// </summary>
    /// <remarks>
    /// Dwa obszary na jednym kalendarzu znaczyłyby, że wydarzenie należy do obu —
    /// a obszar, który nie dzieli, nie jest obszarem. Przełożenie jest ciche, bo odmowa
    /// („ten kalendarz jest już zajęty przez Pracę") zmuszałaby do pójścia tam, zdjęcia
    /// i powrotu, żeby zrobić dokładnie to, co się przed chwilą wybrało.
    /// </remarks>
    [Fact]
    public async Task Kalendarz_stoi_przy_jednym_obszarze()
    {
        var szkielet = new StructureEditService(
            new ProjectRepository(_db), new AreaRepository(_db), new TaskRepository(_db),
            new UnitOfWork(_db), _zegar, _hlc);

        var dzieci = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dzieci", 0);
        var dom = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 1);
        _db.Areas.AddRange(dzieci, dom);
        _db.SaveChanges();

        await szkielet.SetAreaCalendarAsync(dzieci.Id, _zrodlo.Id);
        _db.Areas.Single(o => o.Id == dzieci.Id).CalendarId.Should().Be(_zrodlo.Id);

        // Ten sam kalendarz przy drugim obszarze zdejmuje go z pierwszego.
        await szkielet.SetAreaCalendarAsync(dom.Id, _zrodlo.Id);

        _db.Areas.Single(o => o.Id == dom.Id).CalendarId.Should().Be(_zrodlo.Id);
        _db.Areas.Single(o => o.Id == dzieci.Id).CalendarId.Should().BeNull();
    }

    /// <summary>
    /// Zadanie idzie do kalendarza swojego obszaru, a nie do głównego.
    /// </summary>
    /// <remarks>
    /// Obszar wskazuje kalendarz, więc zadanie z obszaru „Dzieci" ląduje w kalendarzu
    /// rodzinnym samo. Dotąd wszystko szło do jednego głównego: zadanie z pracy
    /// i odbiór dziecka z przedszkola lądowały obok siebie w tym samym wspólnym
    /// kalendarzu, więc udostępnianie było wszystkim-albo-nic i znaczyło w praktyce nic.
    /// </remarks>
    [Fact]
    public async Task Zadanie_idzie_do_kalendarza_swojego_obszaru()
    {
        var ustawienia = new Ustawienia { MainCalendarId = _zrodlo.Id };
        var odbicie = new TaskMirror(
            _usluga, new TaskRepository(_db), new UnitOfWork(_db), _hlc, ustawienia, new Notes(),
            new AreaRepository(_db));

        var rodzinny = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/rodzina.ics", "Rodzina");

        _db.CalendarSources.Add(rodzinny);

        var dzieci = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dzieci", 0);
        dzieci.SetCalendar(rodzinny.Id, _hlc.Next());
        _db.Areas.Add(dzieci);

        var praca = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Praca", 1);
        _db.Areas.Add(praca);

        var odbior = TaskItem.Capture("Odebrać Sanię", _zegar.Now, _hlc.Next());
        odbior.Schedule(dzieci.Id, Dzis, _hlc.Next());
        odbior.SetDoTime(new TimeOnly(16, 0), _hlc.Next());
        _db.Tasks.Add(odbior);

        var raport = TaskItem.Capture("Raport", _zegar.Now, _hlc.Next());
        raport.Schedule(praca.Id, Dzis, _hlc.Next());
        raport.SetDoTime(new TimeOnly(9, 0), _hlc.Next());
        _db.Tasks.Add(raport);
        _db.SaveChanges();

        await odbicie.PushAsync(_db.Tasks.Single(t => t.Id == odbior.Id));
        await odbicie.PushAsync(_db.Tasks.Single(t => t.Id == raport.Id));

        _db.Tasks.Single(t => t.Id == odbior.Id).SharedCalendarId
            .Should().Be(rodzinny.Id, "obszar Dzieci to kalendarz rodzinny");

        // Obszar bez własnego kalendarza spada do głównego — tam idą też wrzuty,
        // których jeszcze nikt nie rozstrzygnął.
        _db.Tasks.Single(t => t.Id == raport.Id).SharedCalendarId.Should().Be(_zrodlo.Id);
    }

    /// <summary>
    /// Przeniesienie do wspólnego zabiera zadanie z głównego, a cofnięcie wraca.
    /// </summary>
    /// <remarks>
    /// Zadanie stoi w jednym kalendarzu naraz. Stanie w dwóch znaczyłoby dwa wpisy
    /// na jedną rzecz w jednym widoku telefonu — i dwa miejsca, w których trzeba by
    /// je potem odhaczyć.
    /// </remarks>
    [Fact]
    public async Task Przeniesienie_do_wspolnego_i_z_powrotem_zostawia_jeden_wpis()
    {
        var ustawienia = new Ustawienia { MainCalendarId = _zrodlo.Id };
        var odbicie = new TaskMirror(
            _usluga, new TaskRepository(_db), new UnitOfWork(_db), _hlc, ustawienia, new Notes(),
            new AreaRepository(_db));

        // Drugi kalendarz zakładany tutaj, nie w konstruktorze: odświeżanie przechodzi
        // po **wszystkich** źródłach tym samym kanałem atrapy, więc stały drugi kalendarz
        // podwajałby wydarzenia w każdym innym teście w tym pliku.
        var wspolny = new CalendarSource(
            Guid.CreateVersion7(), _zegar.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/wspolny.ics", "Rodzina");

        _db.CalendarSources.Add(wspolny);

        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(obszar);

        var zadanie = TaskItem.Capture("Wywiadówka", _zegar.Now, _hlc.Next());
        zadanie.Schedule(obszar.Id, Dzis, _hlc.Next());
        zadanie.SetDoTime(new TimeOnly(17, 0), _hlc.Next());
        _db.Tasks.Add(zadanie);
        _db.SaveChanges();

        await odbicie.PushAsync(_db.Tasks.Single(t => t.Id == zadanie.Id));
        _db.Tasks.Single(t => t.Id == zadanie.Id).SharedCalendarId.Should().Be(_zrodlo.Id);

        (await odbicie.ShareAsync(zadanie.Id, wspolny.Id)).Should().BeNull();

        // Najpierw znika ze starego, dopiero potem powstaje w nowym — inaczej przy
        // nieudanym zapisie zostałyby dwa wpisy na jedną rzecz.
        _pisarz.Wyslane.Select(w => w.Co).Should().ContainInOrder("skasowanie", "utworzenie");
        _db.Tasks.Single(t => t.Id == zadanie.Id).SharedCalendarId.Should().Be(wspolny.Id);

        await odbicie.UnshareAsync(zadanie.Id);

        _db.Tasks.Single(t => t.Id == zadanie.Id).SharedCalendarId
            .Should().Be(_zrodlo.Id, "cofnięcie wraca na główny, a nie znikąd");
        _pisarz.Wyslane[^1].Co.Should().Be("utworzenie");
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

        // Tydzień na telefonie mieści się bez przewijania w bok: przy szerokości,
        // jaką zostawia ekran w pionie, siedem kolumn wychodzi po czterdzieści punktów
        // i tyle ma zostać. Granica jest niżej, więc tu jej nie widać.
        await model.ShowWeekCommand.ExecuteAsync(null);
        model.SetAvailableWidth(294);
        model.ColumnWidth.Should().Be(40, "siedem kolumn z dwustu dziewięćdziesięciu czterech");

        // Dolna granica dopiero przy oknie, w którym kolumna zeszłaby poniżej czytelności:
        // sześć widocznych i jedna ucięta jest lepsze od siedmiu pasków bez treści.
        model.SetAvailableWidth(100);
        model.ColumnWidth.Should().BeGreaterThanOrEqualTo(34);
    }

    [Fact]
    public async Task Zakres_przyciskami_na_szerokim_polem_na_waskim()
    {
        // Nie dwa wyglądy tej samej rzeczy, tylko dwie odpowiedzi na to, ile jest
        // miejsca. Cztery przyciski mówią od razu, jakie są możliwości i który zakres
        // jest teraz — ale w linii ze strzałkami i pobieraniem mieszczą się dopiero
        // przy szerokim oknie. Zawsze dokładnie jedno z dwojga: żadnego naraz ani
        // obu naraz.
        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        model.SetAvailableWidth(1200);
        model.ShowRangeButtons.Should().BeTrue();
        model.ShowRangePicker.Should().BeFalse();

        model.SetAvailableWidth(360);
        model.ShowRangeButtons.Should().BeFalse();
        model.ShowRangePicker.Should().BeTrue();
    }

    [Fact]
    public async Task Pole_zakresu_idzie_za_widokiem()
    {
        // Zakres zmienia się nie tylko z pola wyboru: zejście z miesiąca na dzień robi
        // to samo jednym dotknięciem komórki. Gdyby pole zostało wtedy na „miesiącu",
        // pokazywałoby coś innego niż siatka pod nim — a to jedyne miejsce, w którym
        // widać, jak szeroko patrzymy.
        var model = new CalendarViewModel(_usluga, _zegar, new Notes(), _edycja);
        await model.LoadAsync();

        await model.ShowWeekCommand.ExecuteAsync(null);
        model.Zakres!.Dni.Should().Be(7);

        await model.ShowMonthCommand.ExecuteAsync(null);
        model.Zakres!.Miesiac.Should().BeTrue();

        // Dotknięcie dnia w miesiącu schodzi na jego siatkę godzinową.
        await model.OpenMonthDayCommand.ExecuteAsync(
            new MonthCell(new DateOnly(2026, 9, 16), "16", true, false, [], 0));

        model.Zakres!.Miesiac.Should().BeFalse();
        model.Zakres!.Dni.Should().Be(1);
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
    public async Task Ten_sam_kalendarz_z_dwoch_kont_to_dwa_podlaczenia()
    {
        // Kalendarz udostępniony obu stronom widnieje u każdej pod tym samym adresem.
        // Gdyby konto nie liczyło się do rozpoznania duplikatu, drugie podłączenie
        // po cichu oddawałoby pierwsze — czyli kalendarz służbowy czytany byłby
        // żetonem konta prywatnego, które nie ma do niego prawa.
        var praca = await _usluga.AddAsync(
            CalendarKind.Google, "wspolny@group.calendar.google.com", "Wspólny",
            account: "praca@example.test");

        var dom = await _usluga.AddAsync(
            CalendarKind.Google, "wspolny@group.calendar.google.com", "Wspólny",
            account: "dom@example.test");

        dom.Id.Should().NotBe(praca.Id);

        var zrodla = await _usluga.SourcesAsync();
        zrodla.Where(z => z.ExternalId == "wspolny@group.calendar.google.com")
            .Select(z => z.Account)
            .Should().BeEquivalentTo(["praca@example.test", "dom@example.test"]);
    }

    [Fact]
    public async Task Konto_glowne_i_dodatkowe_to_nie_jest_ten_sam_kalendarz()
    {
        // Ta sama para rodzaj–identyfikator, różne konta: składanie duplikatów przy
        // odświeżaniu ma zostawić oba. Objaw pomyłki byłby cichy — jeden z dwóch
        // kalendarzy przestałby się pobierać, bez śladu na ekranie.
        await _usluga.AddAsync(CalendarKind.Google, "primary", "Mój");
        await _usluga.AddAsync(
            CalendarKind.Google, "primary", "Służbowy", account: "praca@example.test");

        var raport = await _usluga.RefreshAsync(force: true);

        raport.Folded.Should().Be(0);
        (await _usluga.SourcesAsync()).Count(z => z.ExternalId == "primary").Should().Be(2);
    }

    [Fact]
    public void Wpis_miesiaca_stoi_na_tle_w_barwie_swojego_obszaru()
    {
        // Miesiąc czyta się wzrokiem, nie literami: cztery jednakowe linijki w komórce
        // wyglądają tak samo niezależnie od tego, czy to cztery rzeczy z pracy, czy trzy
        // przedszkolne i jedna urzędowa. Barwa obszaru odpowiada na to bez czytania.
        var praca = new MonthEntry("09:00", "Gala", null, false, "#C0392B");
        var dom = new MonthEntry("17:00", "Przedszkole", null, false, "#27AE60");

        var pierwsza = ((SolidColorBrush)praca.Background).Color;
        var druga = ((SolidColorBrush)dom.Background).Color;

        (pierwsza.R, pierwsza.G, pierwsza.B).Should().Be(((byte)0xC0, (byte)0x39, (byte)0x2B));
        (druga.R, druga.G, druga.B).Should().Be(((byte)0x27, (byte)0xAE, (byte)0x60));
    }

    [Fact]
    public void Ten_sam_obszar_ma_w_miesiacu_ten_sam_odcien_co_na_siatce()
    {
        // Barwa służy tu do rozpoznania obszaru, więc rozjechany odcień psuje dokładnie
        // to, po co jest. Krycie wolno mieć inne: pasek w komórce miesiąca ma jedenaście
        // punktów wysokości i przy sile bloku z siatki robi z tygodnia pasiastą ścianę.
        const string barwa = "#8E24AA";

        var blok = new SlotBox(
            "Spotkanie", 0, 30, 0, 100, false, barwa, "09:00", "10:00", null, "śr", null, null, false);

        var wpis = new MonthEntry("09:00", "Spotkanie", null, false, barwa);

        var naSiatce = ((SolidColorBrush)blok.Background).Color;
        var wKomorce = ((SolidColorBrush)wpis.Background).Color;

        (wKomorce.R, wKomorce.G, wKomorce.B).Should().Be((naSiatce.R, naSiatce.G, naSiatce.B));
        wKomorce.A.Should().BeLessThan(naSiatce.A);
    }

    [Fact]
    public void Barwa_nie_do_odczytania_nie_robi_z_wpisu_wyroznionego()
    {
        // Obszar bez barwy i barwa zapisana czymś, czego nie umiemy odczytać, mają
        // wyglądać jak wszystko inne bez barwy. Powrót do krycia pełnego dawał jedyny
        // nieprzezroczysty prostokąt na siatce — czyli wpis wyróżniony za to, że coś
        // z nim nie tak.
        var bezBarwy = new MonthEntry("09:00", "Bez barwy", null, false, null);
        var zeSmieciem = new MonthEntry("09:00", "Ze śmieciem", null, false, "obszar Praca");

        ((SolidColorBrush)zeSmieciem.Background).Color
            .Should().Be(((SolidColorBrush)bezBarwy.Background).Color);
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
