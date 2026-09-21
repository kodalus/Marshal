using Avalonia.Media;
using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Calendar;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Calendar;
using Marshal.Infrastructure.Data;
using Marshal.Application.UseCases;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Marshal.UI;
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
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    /// <summary>Kanał, który oddaje to, co mu się włoży. Bez sieci i bez niespodzianek.</summary>
    private sealed class NewFeed(CalendarKind kind) : ICalendarFeed
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
        public List<(string Operation, string Outcome, ActivityLevel Level)> Entries { get; } = [];

        public int Dropped => 0;

        public Task RecordAsync(
            string operation, string outcome, ActivityLevel level = ActivityLevel.Ok,
            string? detail = null, CancellationToken ct = default)
        {
            Entries.Add((operation, outcome, level));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEntry>> RecentAsync(
            int count = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActivityEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Ustawienia z jedną strefą. Warszawa, bo o nią w tym projekcie chodzi.</summary>
    private sealed class Settings : ISettings
    {
        public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

        public string? ZoneProblem => null;

        public ThemeChoice Theme => ThemeChoice.System;

        public string? GoogleClientId => null;

        public string? GoogleClientSecret => null;

        public bool GoogleCalendarEnabled => false;

        public Guid? MainCalendarId { get; set; }

        public void SetMainCalendar(Guid? calendarId) => MainCalendarId = calendarId;

        public List<string> Accounts { get; } = [];

        public IReadOnlyList<string> CalendarAccounts => Accounts;

        public void AddCalendarAccount(string email) => Accounts.Add(email);

        public void RemoveCalendarAccount(string email) => Accounts.Remove(email);

        public void SetGoogleCalendarEnabled(bool enabled) => throw new NotSupportedException();

        public void SetZone(string id) => throw new NotSupportedException();

        public void SetTheme(ThemeChoice theme) => throw new NotSupportedException();

        public void SetGoogle(string? clientId, string? clientSecret) =>
            throw new NotSupportedException();
    }

    /// <summary>Pisarz, który tylko zapamiętuje, co by wysłał.</summary>
    private sealed class NewWriter : ICalendarWriter
    {
        public List<(string Co, string Title, string? Id, bool AllDay)> Wyslane { get; } = [];

        public bool Rzuca { get; set; }

        /// <summary>Podłączenia, do których ten pisarz odmawia zapisu — jak kalendarz świąteczny.</summary>
        public HashSet<Guid> ReadOnly { get; } = [];

        private void Sprawdz(CalendarSource source)
        {
            if (ReadOnly.Contains(source.Id))
            {
                throw new InvalidOperationException("Ten kalendarz jest tylko do odczytu.");
            }
        }

        public CalendarKind Kind => CalendarKind.Ical;

        public Task<string> CreateAsync(
            CalendarSource source, CalendarDraft draft, CancellationToken ct = default)
        {
            if (Rzuca)
            {
                throw new HttpRequestException("kalendarz nie odpowiada");
            }

            Sprawdz(source);

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

            Sprawdz(source);

            Wyslane.Add(("skasowanie", string.Empty, externalId, false));
            return Task.CompletedTask;
        }

        /// <summary>Kto już jest gościem którego wydarzenia — atrapa listy u źródła.</summary>
        public Dictionary<string, List<string>> Guests { get; } = [];

        public Task<bool> InviteAsync(
            CalendarSource source, string externalId, string email, CancellationToken ct = default)
        {
            if (Rzuca)
            {
                throw new HttpRequestException("kalendarz nie odpowiada");
            }

            if (!Guests.TryGetValue(externalId, out var list))
            {
                list = [];
                Guests[externalId] = list;
            }

            if (list.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            list.Add(email);
            Wyslane.Add(("zaproszenie", email, externalId, false));

            return Task.FromResult(true);
        }
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly HlcSource _hlc;
    private readonly CalendarStore _sklad;
    private readonly NewFeed _feed = new(CalendarKind.Ical);
    private readonly CalendarSyncService _service;
    private readonly CalendarSource _source;
    private readonly NewWriter _writer = new();
    private readonly TaskEditService _edit;

    public CalendarStoreTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_clock, "biurko");
        _sklad = new CalendarStore(_db);

        _service = new CalendarSyncService(
            _sklad, new TaskRepository(_db), [_feed], _clock, _hlc, new Settings(), [_writer],
            new ProjectRepository(_db), new AreaRepository(_db));

        _edit = new TaskEditService(
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _clock, new AreaRepository(_db),
            new NoTaskMirror());

        _source = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(_source);
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
    private DateOnly Today => ((IClock)_clock).Today;

    private static FeedEvent NewEvent(string id, string title, string day, int od, int toHour) =>
        new(id, title,
            new DateTimeOffset(DateOnly.Parse(day).ToDateTime(new TimeOnly(od, 0)), TimeSpan.FromHours(2)),
            new DateTimeOffset(DateOnly.Parse(day).ToDateTime(new TimeOnly(toHour, 0)), TimeSpan.FromHours(2)),
            false, null, false);

    [Fact]
    public async Task Pierwsze_pobranie_zapisuje_wydarzenia()
    {
        _feed.Next = new FeedResult([NewEvent("a", "Zebranie", "2026-09-17", 17, 18)], null, true);

        var report = await _service.RefreshAsync();

        report.Sources.Should().Be(1);
        report.Events.Should().Be(1);
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
        _clock.Now = _clock.Now.AddMinutes(1);

        var zDrugiegoUrzadzenia = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(zDrugiegoUrzadzenia);
        _db.SaveChanges();

        _feed.Next = new FeedResult([NewEvent("a", "Zebranie", "2026-09-17", 17, 18)], null, true);

        var report = await _service.RefreshAsync(force: true);

        report.Folded.Should().Be(1, "zostaje najstarsze podłączenie, reszta odpada");

        // Sedno: wydarzenie ma być jedno. Dwa źródła znaczyły dwa pobrania tego samego
        // kanału, a klucz kopii to para źródło–identyfikator — więc siatka rysowała
        // każde wydarzenie podwójnie, obok siebie, w tych samych barwach.
        _db.CalendarEvents.Should().ContainSingle();

        var zostale = await _sklad.SourcesAsync();
        zostale.Should().ContainSingle().Which.Id.Should().Be(_source.Id);
    }

    [Fact]
    public async Task Skladanie_duplikatow_przepina_to_co_wskazywalo_na_odrzucone()
    {
        // Bez przepięcia złożenie zostawiało wiszące wskazanie i objawiało się dopiero
        // przy zapisie: „tego kalendarza już nie ma na liście podłączonych" przy zadaniu,
        // którego nikt nie ruszał. Odczyt szedł przez to, co zostało, a zapis przez to,
        // co zniknęło.
        _clock.Now = _clock.Now.AddMinutes(1);

        var mlodsze = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(mlodsze);

        var task = TaskItem.Capture("Zebranie", _clock.Now, _hlc.Next());
        task.Share(mlodsze.Id, "zewnetrzny-1", _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        var settings = new Settings();
        settings.SetMainCalendar(mlodsze.Id);

        var service = new CalendarSyncService(
            _sklad, new TaskRepository(_db), [_feed], _clock, _hlc, settings, [_writer],
            new ProjectRepository(_db), new AreaRepository(_db));

        _feed.Next = new FeedResult([], null, true);

        (await service.RefreshAsync(force: true)).Folded.Should().Be(1);

        // Obie strony wskazywały **ten sam** kalendarz u źródła — na tym polega bycie
        // duplikatem — więc identyfikator wydarzenia zostaje ważny.
        settings.MainCalendarId.Should().Be(_source.Id);

        var przepiete = _db.Tasks.Single(t => t.Id == task.Id);
        przepiete.SharedCalendarId.Should().Be(_source.Id);
        przepiete.SharedEventId.Should().Be("zewnetrzny-1");
    }

    [Fact]
    public async Task Wskazanie_na_odrzucone_podlaczenie_jest_starym_imieniem_zyjacego()
    {
        // Ten sam kalendarz Google podłączony na dwóch urządzeniach ma na każdym własny
        // identyfikator wiersza. Wybór kalendarza się synchronizuje, więc zadanie
        // udostępnione na komputerze przyjeżdża na telefon ze wskazaniem na wiersz
        // komputera — a składanie duplikatów robi z jednego z nich nagrobek.
        _clock.Now = _clock.Now.AddMinutes(1);

        var zDrugiegoUrzadzenia = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(zDrugiegoUrzadzenia);
        _db.SaveChanges();

        _feed.Next = new FeedResult([], null, true);
        (await _service.RefreshAsync(force: true)).Folded.Should().Be(1);

        // Sedno: wskazanie na nagrobek nie jest brakiem kalendarza. Odmowa przy nim
        // wyglądała absurdalnie — wszystkie kalendarze wyświetlały się poprawnie,
        // a zadania z drugiego urządzenia nie dawały się ani zapisać, ani skasować.
        (await _service.LiveCalendarAsync(zDrugiegoUrzadzenia.Id)).Should().Be(_source.Id);
        (await _service.LiveCalendarAsync(_source.Id)).Should().Be(_source.Id);
    }

    [Fact]
    public async Task Podlaczenie_ktorego_naprawde_nie_ma_zostaje_puste()
    {
        // Jedyny przypadek, w którym zapis ma odmówić: odłączono ręcznie i nic nie
        // zajęło miejsca. Bez tego rozróżnienia naprawa przykrywałaby prawdziwy brak.
        (await _service.LiveCalendarAsync(Guid.CreateVersion7())).Should().BeNull();
    }

    [Fact]
    public async Task Odswiezenie_naprawia_wiszacy_kalendarz_glowny()
    {
        // Składanie duplikatów przepina to, co widzi w chwili składania. Aplikacja,
        // w której złożenie odbyło się przed tą poprawką, zostałaby ze wskazaniem
        // wiszącym na zawsze — stąd naprawa przy każdym odświeżeniu.
        _clock.Now = _clock.Now.AddMinutes(1);

        var mlodsze = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        mlodsze.MarkDeleted(_hlc.Next());
        _db.CalendarSources.Add(mlodsze);
        _db.SaveChanges();

        var settings = new Settings { MainCalendarId = mlodsze.Id };

        var service = new CalendarSyncService(
            _sklad, new TaskRepository(_db), [_feed], _clock, _hlc, settings, [_writer],
            new ProjectRepository(_db), new AreaRepository(_db));

        _feed.Next = new FeedResult([], null, true);
        await service.RefreshAsync(force: true);

        settings.MainCalendarId.Should().Be(_source.Id);
    }

    [Fact]
    public async Task Skladanie_duplikatow_kasuje_kopie_odrzuconego_zrodla()
    {
        // Odrzucone źródło zostawiało po sobie wydarzenia: niewidoczne, bo źródła już
        // nie ma, ale policzone w „ile w bazie" — czyli mylące dokładnie w tym miejscu,
        // w którym się patrzy, szukając duplikatów.
        _clock.Now = _clock.Now.AddMinutes(1);

        var drugie = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole");
        _db.CalendarSources.Add(drugie);
        _db.CalendarEvents.Add(new CalendarEvent(
            drugie.Id, "a", "Zebranie",
            new DateTimeOffset(2026, 9, 17, 17, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 17, 18, 0, 0, TimeSpan.FromHours(2)),
            false));
        _db.SaveChanges();

        _feed.Next = new FeedResult([], null, true);

        await _service.RefreshAsync(force: true);

        _db.CalendarEvents.Should().BeEmpty();
        (await _sklad.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Ponowne_pobranie_aktualizuje_zamiast_dublowac()
    {
        // Klucz to para źródło–identyfikator zewnętrzny, więc to samo wydarzenie
        // przychodzące drugi raz ma się zaktualizować, a nie dopisać.
        _feed.Next = new FeedResult([NewEvent("a", "Zebranie", "2026-09-17", 17, 18)], null, true);
        await _service.RefreshAsync();

        _feed.Next = new FeedResult([NewEvent("a", "Zebranie przełożone", "2026-09-17", 18, 19)], null, true);
        await _service.RefreshAsync(force: true);

        _db.CalendarEvents.Should().ContainSingle();
        _db.CalendarEvents.Single().Title.Should().Be("Zebranie przełożone");
    }

    [Fact]
    public async Task Pelne_pobranie_oznacza_zniknione_jako_odwolane()
    {
        _feed.Next = new FeedResult(
            [NewEvent("a", "Zostaje", "2026-09-17", 17, 18),
             NewEvent("b", "Znika", "2026-09-18", 17, 18)], null, true);
        await _service.RefreshAsync();

        _feed.Next = new FeedResult([NewEvent("a", "Zostaje", "2026-09-17", 17, 18)], null, true);
        await _service.RefreshAsync(force: true);

        // Nagrobek, nie usunięcie — tak samo jak wszędzie indziej w tym modelu.
        _db.CalendarEvents.Should().HaveCount(2);
        _db.CalendarEvents.Single(e => e.ExternalId == "b").Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Pobranie_przyrostowe_nie_sprzata()
    {
        // Przy przyrostowym „nie przyszło" znaczy „bez zmian". To samo sprzątanie
        // skasowałoby cały kalendarz przy pierwszym pobraniu, w którym nic się nie zmieniło.
        _feed.Next = new FeedResult([NewEvent("a", "Zostaje", "2026-09-17", 17, 18)], "zeton", true);
        await _service.RefreshAsync();

        _feed.Next = new FeedResult([], "zeton2", IsFull: false);
        await _service.RefreshAsync(force: true);

        _db.CalendarEvents.Single().Cancelled.Should().BeFalse();
    }

    [Fact]
    public async Task Zeton_jest_zapamietywany_i_podawany_dalej()
    {
        _feed.Next = new FeedResult([], "zeton-1", true);
        await _service.RefreshAsync();

        (await _sklad.CursorAsync(_source.Id))!.SyncToken.Should().Be("zeton-1");
    }

    [Fact]
    public async Task Swiezo_pobrany_kalendarz_nie_jest_pobierany_znowu()
    {
        await _service.RefreshAsync();
        await _service.RefreshAsync();

        _feed.Wywolan.Should().Be(1);
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
        await _service.RefreshAsync();
        _clock.Now = _clock.Now.Add(CalendarSyncService.RefreshInterval).AddMinutes(1);
        await _service.RefreshAsync();

        _feed.Wywolan.Should().Be(2);
    }

    [Fact]
    public async Task Niedostepny_kanal_nie_wywraca_odswiezania()
    {
        // Kalendarz jest dodatkiem do zadań. Niedostępny kanał ma znaczyć „brak świeżych
        // wydarzeń", a nie „aplikacja się nie otwiera".
        _feed.Rzuca = true;

        var report = await _service.RefreshAsync();

        report.Failed.Should().Be(1);
        report.Sources.Should().Be(0);
    }

    [Fact]
    public async Task Ukryty_kalendarz_znika_z_siatki_ale_zostaje_w_bazie()
    {
        _feed.Next = new FeedResult([NewEvent("a", "Zebranie", "2026-09-16", 17, 18)], null, true);
        await _service.RefreshAsync();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Should().ContainSingle();

        _source.SetVisible(false, _hlc.Next());
        _db.SaveChanges();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Should().BeEmpty();
        _db.CalendarEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Odwolane_wydarzenie_nie_trafia_na_siatke()
    {
        _feed.Next = new FeedResult(
            [NewEvent("a", "Odwołane", "2026-09-16", 17, 18) with { Cancelled = true }], null, true);
        await _service.RefreshAsync();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Should().BeEmpty();
    }

    /// <summary>
    /// Rytm rysuje się do przodu na całym oglądanym zakresie.
    /// </summary>
    /// <remarks>
    /// W modelu żyje naraz jedno wystąpienie serii — regułę nosi najnowsze — więc siatka
    /// pokazywała rytm raz, inaczej niż wydarzenie cykliczne z Google, które rozwija
    /// u siebie Google. Przyszłe wystąpienia są wyliczane przy rysowaniu i niczego nie
    /// zapisują: nie mają własnego identyfikatora, tylko wskazanie zadania z regułą.
    /// </remarks>
    [Fact]
    public async Task Rytm_rysuje_sie_na_przyszlych_dniach()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        // Szesnasty września 2026 to środa.
        var task = TaskItem.Capture("Śmieci", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        task.SetDoTime(new TimeOnly(19, 0), _hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Wednesday),
            _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();

        var drawn = (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 21))
            .SelectMany(d => d.Timed)
            .Select(s => s.Entry)
            .Where(e => e.Title == "Śmieci")
            .ToList();

        drawn.Select(e => e.Start.Date).Should().Equal(
            new DateTime(2026, 9, 16), new DateTime(2026, 9, 23), new DateTime(2026, 9, 30));

        drawn[0].TaskId.Should().Be(task.Id, "pierwsze wystąpienie jest prawdziwym zadaniem");
        drawn[0].IsAhead.Should().BeFalse();

        drawn.Skip(1).Should().OnlyContain(
            e => e.TaskId == null && e.RhythmId == task.Id && e.IsAhead);

        drawn.Should().OnlyContain(e => e.Start.Hour == 19, "pora rytmu jest ta sama");
    }

    /// <summary>
    /// Rytm widać także wtedy, gdy zadanie niosące regułę jest poza oglądanym zakresem.
    /// </summary>
    /// <remarks>
    /// To jest cały powód, dla którego rytmy wczytują się osobnym pytaniem, a nie
    /// przesiewem tego, co i tak wczytane na te dni. Rytm zaczepiony na dzisiaj ma się
    /// rysować również wtedy, gdy przewinie się kalendarz o miesiąc do przodu.
    /// </remarks>
    [Fact]
    public async Task Rytm_widac_gdy_jego_zadanie_jest_poza_zakresem()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Śmieci", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        task.SetDoTime(new TimeOnly(19, 0), _hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Wednesday),
            _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();

        var drawn = (await _service.AgendaAsync(new DateOnly(2026, 10, 5), 7))
            .SelectMany(d => d.Timed)
            .Select(s => s.Entry)
            .Where(e => e.Title == "Śmieci")
            .ToList();

        drawn.Should().ContainSingle();
        drawn[0].Start.Date.Should().Be(new DateTime(2026, 10, 7));
        drawn[0].RhythmId.Should().Be(task.Id);
    }

    /// <summary>
    /// Seria policzona na wystąpienia nie rysuje się dłużej, niż trwa.
    /// </summary>
    /// <remarks>
    /// Licznik liczy z bieżącym wystąpieniem, więc dwójka znaczy „to i jeszcze jedno".
    /// Narysowana zapowiedź musi kończyć się tam, gdzie skończy się seria — inaczej
    /// obiecywałaby coś, co przy odhaczaniu nigdy nie powstanie.
    /// </remarks>
    [Fact]
    public async Task Rytm_policzony_na_wystapienia_konczy_rysowanie_razem_z_seria()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Kurs", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        task.SetDoTime(new TimeOnly(18, 0), _hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Wednesday, count: 2),
            _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();

        var drawn = (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 28))
            .SelectMany(d => d.Timed)
            .Select(s => s.Entry)
            .Where(e => e.Title == "Kurs")
            .ToList();

        drawn.Select(e => e.Start.Date).Should().Equal(
            new DateTime(2026, 9, 16), new DateTime(2026, 9, 23));
    }

    /// <summary>
    /// Odwołane wystąpienie znika z siatki, a rytm zostaje.
    /// </summary>
    /// <remarks>
    /// „W tę jedną środę nie" nie miało dotąd jak się wydarzyć: albo zmieniało się regułę
    /// (czyli wszystkie pozostałe środy), albo nie robiło nic. Zmiana zapisuje się przy
    /// dniu z reguły i nie rusza ani rytmu, ani żadnego innego wystąpienia.
    /// </remarks>
    [Fact]
    public async Task Odwolane_wystapienie_znika_z_siatki()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Śmieci", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        task.SetDoTime(new TimeOnly(19, 0), _hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(
                RecurrenceKind.Weekly,
                daysOfWeek: Weekdays.Wednesday,
                changes: [new RecurrenceChange(new DateOnly(2026, 9, 23), Dropped: true)]),
            _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 21))
            .SelectMany(d => d.Timed)
            .Select(s => s.Entry)
            .Where(e => e.Title == "Śmieci")
            .Select(e => e.Start.Date)
            .Should().Equal(new DateTime(2026, 9, 16), new DateTime(2026, 9, 30));
    }

    /// <summary>
    /// Przełożone wystąpienie stoi tam, dokąd je przełożono — z własną porą.
    /// </summary>
    /// <remarks>
    /// Tożsamość zostaje przy dniu z reguły, bo po nim rozpoznaje się zmianę. Bez tego
    /// przełożenie dwa razy z rzędu gubiłoby ślad, po którym wiadomo, czego dotyczy.
    /// </remarks>
    [Fact]
    public async Task Przelozone_wystapienie_stoi_w_nowym_dniu_i_o_nowej_porze()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Śmieci", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        task.SetDoTime(new TimeOnly(19, 0), _hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(
                RecurrenceKind.Weekly,
                daysOfWeek: Weekdays.Wednesday,
                changes:
                [
                    new RecurrenceChange(
                        new DateOnly(2026, 9, 23),
                        Day: new DateOnly(2026, 9, 24),
                        Time: new TimeOnly(17, 0)),
                ]),
            _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();

        var drawn = (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 14))
            .SelectMany(d => d.Timed)
            .Select(s => s.Entry)
            .Where(e => e.Title == "Śmieci")
            .ToList();

        drawn.Select(e => e.Start.Date).Should().Equal(
            new DateTime(2026, 9, 16), new DateTime(2026, 9, 24));

        drawn[1].Start.Hour.Should().Be(17);
        drawn[1].RhythmDate.Should().Be(new DateOnly(2026, 9, 23));
        drawn[1].RhythmId.Should().Be(task.Id);
    }

    /// <summary>
    /// Zadanie bez rytmu nie dorabia sobie przyszłych wystąpień.
    /// </summary>
    [Fact]
    public async Task Zadanie_bez_rytmu_rysuje_sie_raz()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Wywiadówka", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        task.SetDoTime(new TimeOnly(17, 0), _hlc.Next());

        _db.Tasks.Add(task);
        _db.SaveChanges();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 21))
            .SelectMany(d => d.Timed)
            .Count(s => s.Entry.Title == "Wywiadówka")
            .Should().Be(1);
    }

    [Fact]
    public async Task Zadanie_z_godzina_laduje_na_siatce_a_bez_godziny_na_pasku()
    {
        // Zgadywanie godziny zrobiłoby z listy zadań kalendarz, w którym wszystko jest
        // umówione — a to jest ten rodzaj planowania, który się nie utrzymuje.
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var withHour = TaskItem.Capture("Zadzwonić", _clock.Now, _hlc.Next());
        withHour.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        withHour.SetDoTime(new TimeOnly(14, 0), _hlc.Next());

        var withoutTime = TaskItem.Capture("Kupić mleko", _clock.Now, _hlc.Next());
        withoutTime.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());

        _db.Tasks.AddRange(withHour, withoutTime);
        _db.SaveChanges();

        var day = (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0];

        day.Timed.Should().ContainSingle();
        day.Timed[0].Entry.Title.Should().Be("Zadzwonić");
        day.Timed[0].Entry.StartHour.Should().Be(14);
        day.AllDay.Select(e => e.Title).Should().Equal("Kupić mleko");
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
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        area.SetColor("#3FA36B", _hlc.Next());
        _db.Areas.Add(area);

        var project = new Project(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Opony na aucie", area.Id, 0);
        project.SetColor("#CF5757", _hlc.Next());
        _db.Projects.Add(project);

        var fromArea = TaskItem.Capture("Z obszaru", _clock.Now, _hlc.Next());
        fromArea.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        fromArea.SetDoTime(new TimeOnly(9, 0), _hlc.Next());

        var fromProject = TaskItem.Capture("Z projektu", _clock.Now, _hlc.Next());
        fromProject.Schedule(area.Id, new DateOnly(2026, 9, 16), _hlc.Next());
        fromProject.SetDoTime(new TimeOnly(11, 0), _hlc.Next());
        fromProject.MoveTo(area.Id, project.Id, _hlc.Next());

        _db.Tasks.AddRange(fromArea, fromProject);
        _db.SaveChanges();

        var day = (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0];

        day.Timed.Single(s => s.Entry.Title == "Z obszaru").Entry.Color.Should().Be("#3FA36B");
        day.Timed.Single(s => s.Entry.Title == "Z projektu").Entry.Color.Should().Be("#CF5757");
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
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Zrobione", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        await _edit.CompleteAsync(task.Id);
        await _edit.RescheduleAsync(task.Id, Today, new TimeOnly(15, 30));

        var poPrzeciagnieciu = _db.Tasks.Single(t => t.Id == task.Id);
        poPrzeciagnieciu.State.Should().Be(TaskState.Done);
        poPrzeciagnieciu.DoTime.Should().Be(new TimeOnly(15, 30));

        (await _service.AgendaAsync(Today, 1))[0].Timed
            .Single(b => b.Entry.TaskId == task.Id).Entry.IsDone.Should().BeTrue();
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
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Pomyłka", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        await _edit.CompleteAsync(task.Id);
        await _edit.ReopenAsync(task.Id);

        var poZdjeciu = _db.Tasks.Single(t => t.Id == task.Id);
        poZdjeciu.State.Should().Be(TaskState.Scheduled);
        poZdjeciu.CompletedAt.Should().BeNull();

        (await _service.AgendaAsync(Today, 1))[0].Timed
            .Single(b => b.Entry.TaskId == task.Id).Entry.IsDone.Should().BeFalse();
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
        _feed.Next = new FeedResult(
            [NewEvent("s1", "Spotkanie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        await _service.SetEventDoneAsync(_source.Id, "s1", done: true);

        _writer.Wyslane.Should().ContainSingle()
            .Which.Should().Be(("nazwa", "✓ Spotkanie", "s1", false));

        var entry = (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Single();
        entry.Entry.IsDone.Should().BeTrue();

        // Sam ptaszek nie jest częścią nazwy: obok kwadracika stałby drugi raz,
        // a zmiana nazwy w oknie odesłałaby go do Google zapisanego podwójnie.
        entry.Entry.Title.Should().Be("Spotkanie");
    }

    [Fact]
    public async Task Zdjecie_ptaszka_z_wydarzenia_wraca_do_czystej_nazwy()
    {
        _feed.Next = new FeedResult(
            [NewEvent("s1", "✓ Spotkanie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
            .Timed.Single().Entry.IsDone.Should().BeTrue();

        await _service.SetEventDoneAsync(_source.Id, "s1", done: false);

        _writer.Wyslane.Should().ContainSingle()
            .Which.Should().Be(("nazwa", "Spotkanie", "s1", false));

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
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
        _feed.Next = new FeedResult(
            [NewEvent("s1", "Spotkanie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);
        _writer.Rzuca = true;

        var complete = async () => await _service.SetEventDoneAsync(_source.Id, "s1", done: true);

        await complete.Should().ThrowAsync<HttpRequestException>();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
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
    /// <summary>
    /// Wejście z kafelka w wybrane zadanie pokazuje tydzień, w którym ono leży.
    /// </summary>
    /// <remarks>
    /// Kafelek ma strzałki i pokazuje dowolny tydzień, a zadanie bywa zaległe. Bez
    /// przestawienia zakresu dotknięcie wpisu sprzed dwóch tygodni otwierało kartę nad
    /// tygodniem bieżącym — a po jej zamknięciu zostawał widok, który z dotkniętym
    /// zadaniem nie miał nic wspólnego.
    /// </remarks>
    [Fact]
    public async Task Wejscie_w_zadanie_przestawia_kalendarz_na_jego_tydzien()
    {
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        var day = Today.AddDays(17);

        await model.ShowAsync(day, new TimeOnly(10, 0));

        model.Anchor.Should().Be(
            day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            "tydzień zaczyna się w poniedziałek, a pokazać ma ten, w którym leży zadanie");

        model.Columns.Select(k => k.Date).Should().Contain(day, "dzień zadania ma być na siatce");
    }

    /// <summary>Wejście z kafelka nie przestawia zakresu, tylko go przesuwa.</summary>
    /// <remarks>
    /// Kto ogląda jeden dzień, ten po wejściu z kafelka dalej ogląda jeden dzień —
    /// ten właściwy. Przestawienie zakresu przy okazji byłoby drugą zmianą pod jednym
    /// dotknięciem i do poprzedniego widoku trzeba by wracać ręcznie.
    /// </remarks>
    [Fact]
    public async Task Wejscie_w_zadanie_zostawia_zakres_taki_jaki_byl()
    {
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();
        await model.ShowDayCommand.ExecuteAsync(null);

        var day = Today.AddDays(-9);

        await model.ShowAsync(day);

        model.VisibleDays.Should().Be(1);
        model.Anchor.Should().Be(day);
    }

    [Fact]
    public async Task Rozciagniecie_bloku_zmienia_dlugosc_a_nie_pore()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Spotkanie", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        task.SetEstimate(30, Energy.Medium, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        var block = model.Columns.SelectMany(k => k.Slots).Single(b => b.TaskId == task.Id);

        // Dolna krawędź ściągnięta na 11:20. Godzina zaczepienia zostaje, długość rośnie.
        await model.ResizeAsync(block, (11 * 48) + (20 * 48 / 60.0));

        var po = _db.Tasks.Single(t => t.Id == task.Id);
        po.DoTime.Should().Be(new TimeOnly(10, 0), "rozciąganie nie rusza pory zaczepienia");
        po.EstimatedMinutes.Should().Be(80);
        po.Energy.Should().Be(Energy.Medium, "zmieniamy jedno pole, nie dwa");
    }

    /// <summary>Pociągnięcie krawędzi ponad początek daje najkrótszy blok, nie ujemny.</summary>
    [Fact]
    public async Task Rozciagniecie_ponad_poczatek_daje_najkrotszy_blok()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Spotkanie", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        task.SetEstimate(60, Energy.Medium, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        var block = model.Columns.SelectMany(k => k.Slots).Single(b => b.TaskId == task.Id);

        await model.ResizeAsync(block, 8 * 48);

        _db.Tasks.Single(t => t.Id == task.Id).EstimatedMinutes.Should().Be(5);
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
        var mirror = new TaskMirror(
            _service, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Settings(), new Notes(),
            new AreaRepository(_db));

        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Odebrać Sanię", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(16, 0), _hlc.Next());
        task.SetEstimate(30, Energy.Medium, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        (await mirror.ShareAsync(task.Id, _source.Id)).Should().BeNull("miało się udać");

        _writer.Wyslane.Should().ContainSingle()
            .Which.Should().Be(("utworzenie", "Odebrać Sanię", (string?)null, false));

        _db.Tasks.Single(t => t.Id == task.Id).SharedEventId.Should().Be("nowe-1");

        // Przesunięcie po siatce ma dojść do kalendarza, bo tam ktoś na to patrzy.
        var edit = new TaskEditService(
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _clock,
            new AreaRepository(_db), mirror);

        await edit.RescheduleAsync(task.Id, Today, new TimeOnly(17, 30));

        _writer.Wyslane.Should().HaveCount(2);
        _writer.Wyslane[1].Co.Should().Be("zmiana");

        // Odhaczenie dokłada ptaszek do nazwy — druga osoba widzi, że zrobione.
        await edit.CompleteAsync(task.Id);
        _writer.Wyslane[^1].Title.Should().Be("✓ Odebrać Sanię");

        // I najważniejsze: na siatce stoi **jeden** blok, nie dwa. Zapis do kalendarza
        // wraca do naszej kopii jako wydarzenie; narysowane obok zadania dałoby dwa
        // bloki na tę samą rzecz, z których jeden nie dawałby się odhaczyć.
        (await _service.AgendaAsync(Today, 1))[0].Timed
            .Should().ContainSingle()
            .Which.Entry.TaskId.Should().Be(task.Id);
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
        var mirror = new TaskMirror(
            _service, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Settings(), new Notes(),
            new AreaRepository(_db));

        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Odwołane", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(9, 0), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        await mirror.ShareAsync(task.Id, _source.Id);

        var skrzynka = new InboxService(
            new TaskRepository(_db), new ProjectRepository(_db), new UnitOfWork(_db),
            _clock, _hlc, mirror);

        await skrzynka.TrashAsync(task.Id);

        _writer.Wyslane[^1].Co.Should().Be("skasowanie");

        var po = _db.Tasks.Single(t => t.Id == task.Id);
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
        var mirror = new TaskMirror(
            _service, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Settings(), new Notes(),
            new AreaRepository(_db));

        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Zostanie sierotą", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(11, 0), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        await mirror.ShareAsync(task.Id, _source.Id);

        // Wyrzucenie bez odbicia — dokładnie to, co zostaje po odłożonym kasowaniu,
        // które nie doszło do skutku: zadanie w koszu, wskazanie na wydarzenie całe.
        var skrzynka = new InboxService(
            new TaskRepository(_db), new ProjectRepository(_db), new UnitOfWork(_db),
            _clock, _hlc, new NoTaskMirror());

        await skrzynka.TrashAsync(task.Id);

        // Z ekranu nie widać nic i tak ma być: wskazanie w zadaniu jeszcze jest, więc
        // wydarzenie nadal jest cieniem, a nie osobnym wpisem. Dopóki kasowanie się nie
        // powiodło, siatka czeka na skutek, zamiast pokazywać rzecz w połowie drogi.
        //
        // Ten test opisywał kiedyś objaw odwrotny — wpis stojący dalej, ale już nie jako
        // zadanie — i objaw ten był całą usterką, a nie umową.
        (await _service.AgendaAsync(Today, 1))[0].Timed
            .Should().BeEmpty("cień nie staje się osobnym wpisem, dopóki wskazanie trwa");

        // Pierwsze podejście pada — sieć nie odpowiada. Ma nie rzucić wyżej: dokańczanie
        // chodzi z minutnika i wywrotka zabrałaby ze sobą wszystko, co robi się obok.
        _writer.Rzuca = true;
        await mirror.FinishDeletionAsync();

        _db.Tasks.Single(t => t.Id == task.Id).SharedEventId
            .Should().NotBeNull("nieudane kasowanie nie ma prawa zapomnieć wskazania");

        // Drugie dochodzi — i wtedy znika i u źródła, i u nas.
        _writer.Rzuca = false;
        await mirror.FinishDeletionAsync();

        _writer.Wyslane[^1].Co.Should().Be("skasowanie");

        var po = _db.Tasks.Single(t => t.Id == task.Id);
        po.SharedCalendarId.Should().BeNull();
        po.SharedEventId.Should().BeNull();

        (await _service.AgendaAsync(Today, 1))[0].Timed.Should().BeEmpty();
    }

    /// <summary>Bez daty nie ma czego udostępnić — i mówimy to wprost.</summary>
    /// <remarks>
    /// Sama godzina nie jest już wymagana: zadanie z dniem, ale bez pory, idzie jako
    /// wydarzenie całodniowe. Bez **daty** kalendarz nadal nie ma gdzie go postawić.
    /// </remarks>
    [Fact]
    public async Task Zadanie_bez_daty_nie_da_sie_udostepnic()
    {
        var mirror = new TaskMirror(
            _service, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Settings(), new Notes(),
            new AreaRepository(_db));

        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Kiedyś, nie wiadomo kiedy", _clock.Now, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        (await mirror.ShareAsync(task.Id, _source.Id))
            .Should().Contain("Najpierw dzień");

        _writer.Wyslane.Should().BeEmpty("odmowa nie dotyka kalendarza");
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
        var settings = new Settings { MainCalendarId = _source.Id };
        var mirror = new TaskMirror(
            _service, new TaskRepository(_db), new UnitOfWork(_db), _hlc, settings, new Notes(),
            new AreaRepository(_db));

        var edit = new TaskEditService(
            new TaskRepository(_db), new UnitOfWork(_db), _hlc, _clock,
            new AreaRepository(_db), mirror);

        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Odebrać Sanię", _clock.Now, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        // Sam dzień wystarczy — idzie jako wydarzenie całodniowe. Zgadnięta godzina
        // zrobiłaby z zadania spotkanie, którego nikt nie umawiał.
        await edit.RescheduleAsync(task.Id, Today, null);

        _writer.Wyslane.Should().ContainSingle()
            .Which.Should().Match<(string Co, string Title, string? Id, bool AllDay)>(
                w => w.Co == "utworzenie" && w.AllDay);

        // Dopisana godzina zmienia to samo wydarzenie, a nie zakłada drugiego.
        await edit.RescheduleAsync(task.Id, Today, new TimeOnly(16, 0));

        _writer.Wyslane.Should().HaveCount(2);
        _writer.Wyslane[^1].Co.Should().Be("zmiana");
        _writer.Wyslane[^1].AllDay.Should().BeFalse("od tej chwili ma swoją porę");
        _db.Tasks.Single(t => t.Id == task.Id).SharedCalendarId.Should().Be(_source.Id);
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
        var mirror = new TaskMirror(
            _service, new TaskRepository(_db), new UnitOfWork(_db), _hlc, new Settings(), new Notes(),
            new AreaRepository(_db));

        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Wzięte na dziś", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        await mirror.ShareAsync(task.Id, _source.Id);

        // Jeden blok: zadanie. Jego wydarzenie w Google jest cieniem i nie rysuje się.
        (await _service.AgendaAsync(Today, 1))[0].AllDay
            .Should().ContainSingle().Which.TaskId.Should().Be(task.Id);

        // Zdjęcie z dnia — bez zdejmowania odbicia, czyli dokładnie stan z tej sekundy,
        // w której zadanie już zniknęło, a odpowiedź z sieci jeszcze nie wróciła.
        _db.Tasks.Single(t => t.Id == task.Id).LeaveAsDebt(_hlc.Next());
        _db.SaveChanges();

        (await _service.AgendaAsync(Today, 1))[0].AllDay
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
        _feed.Next = new FeedResult(
            [NewEvent("s1", "Lekarz", Today.ToString("yyyy-MM-dd"), 10, 11)],
            SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Kupić mleko", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(8, 0), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        var plan = new DayPlanService(
            new TaskRepository(_db), new ProjectRepository(_db), new AreaRepository(_db),
            _clock, _service);

        var rows = await plan.ForDayAsync(Today);

        // Po godzinach, niezależnie od tego, skąd wpis pochodzi — dzień ma jedną oś.
        rows.Select(p => p.Title).Should().Equal("Kupić mleko", "Lekarz");

        rows.Single(p => p.Title == "Lekarz").TaskId.Should().BeNull();
        rows.Single(p => p.Title == "Kupić mleko").TaskId.Should().Be(task.Id);

        // Wydarzenie niesie swoje pochodzenie, bo ptaszek idzie do Google, a nie do bazy —
        // i da się je odhaczyć z kafelka tak samo jak z siatki w oknie.
        var doctor = rows.Single(p => p.Title == "Lekarz");

        doctor.SourceId.Should().Be(_source.Id);
        doctor.ExternalId.Should().Be("s1");
        doctor.CanComplete.Should().BeTrue("do tego kalendarza umiemy pisać");
    }

    /// <summary>
    /// Ten sam wpis widziany przez dwa podłączenia rysuje się raz.
    /// </summary>
    /// <remarks>
    /// Kalendarz udostępniony bywa podłączony dwa razy — raz z jednego konta, raz
    /// z drugiego. To są naprawdę dwa różne podłączenia, więc składanie powtórzonych
    /// ich nie łączy; wpis jest jednak jeden. Narysowany dwa razy wygląda na dwa
    /// spotkania o tej samej porze, a gdy jedno z podłączeń ma przypisany obszar,
    /// a drugie nie — na dwie różne rzeczy o tej samej nazwie.
    /// </remarks>
    [Fact]
    public async Task Ten_sam_wpis_z_dwoch_podlaczen_rysuje_sie_raz()
    {
        // Z **innego konta**, bo inaczej składanie powtórzonych podłączeń odrzuciłoby
        // to drugie jako duplikat — i słusznie: ten sam kalendarz na tym samym koncie
        // podłączony dwa razy to jedno podłączenie za dużo. Tu chodzi o przypadek,
        // w którym oba podłączenia są prawdziwe, a powtórzony jest sam wpis.
        var drugie = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/kanal.ics", "Przedszkole (drugie konto)",
            account: "sylw@example.test");

        _db.CalendarSources.Add(drugie);

        // Obszar wskazuje **drugie** podłączenie, żeby było widać, że zostaje to
        // z obszarem, a nie po prostu pierwsze z brzegu.
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        area.SetCalendar(drugie.Id, _hlc.Next());
        _db.Areas.Add(area);
        _db.SaveChanges();

        _feed.Next = new FeedResult(
            [NewEvent("s1", "Wywiadówka", Today.ToString("yyyy-MM-dd"), 17, 18)],
            SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        var timed = (await _service.AgendaAsync(Today, 1))[0].Timed
            .Select(s => s.Entry)
            .Where(e => e.Title == "Wywiadówka")
            .ToList();

        timed.Should().ContainSingle("wpis jest jeden, choć doszedł dwiema drogami");
        timed[0].SourceId.Should().Be(drugie.Id, "zostaje kopia z kalendarza, który ma obszar");
    }

    /// <summary>
    /// Wydarzenie z kalendarza tylko do odczytu nie dostaje kwadracika na kafelku.
    /// </summary>
    /// <remarks>
    /// Ptaszek wydarzenia to zmiana jego nazwy u źródła. Tam, gdzie nie wolno nam pisać,
    /// nie miałby gdzie wylądować — a kwadracik, który kończy się wyłącznie odmową,
    /// jest gorszy od jego braku. Ta sama zasada co w oknie i to jest cały sens:
    /// kafelek ma mówić o wpisie to samo, co siatka.
    /// </remarks>
    [Fact]
    public async Task Wydarzenie_tylko_do_odczytu_nie_ma_czego_odhaczyc_na_kafelku()
    {
        _source.SetReadOnly(true);
        _db.SaveChanges();

        _feed.Next = new FeedResult(
            [NewEvent("s1", "Dzień wolny", Today.ToString("yyyy-MM-dd"), 10, 11)],
            SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        var plan = new DayPlanService(
            new TaskRepository(_db), new ProjectRepository(_db), new AreaRepository(_db),
            _clock, _service);

        var row = (await plan.ForDayAsync(Today)).Single(p => p.Title == "Dzień wolny");

        row.SourceId.Should().Be(_source.Id, "wpis wie, skąd pochodzi, nawet gdy nie da się go zmienić");
        row.CanComplete.Should().BeFalse("do tego kalendarza nie wolno nam pisać");
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
        _feed.Next = new FeedResult(
            [NewEvent("s1", "Wywiadówka", "2026-09-16", 17, 18)], SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        (await _service.InviteAsync(_source.Id, "s1", "sylw@example.test"))
            .Should().BeTrue("tej osoby jeszcze nie było na liście");

        _writer.Wyslane[^1].Should().Be(("zaproszenie", "sylw@example.test", "s1", false));

        (await _service.InviteAsync(_source.Id, "s1", "sylw@example.test"))
            .Should().BeFalse("już to widzi");

        _writer.Guests["s1"].Should().ContainSingle();
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
        _feed.Next = new FeedResult(
            [NewEvent("s1", "Zebranie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        var rodzinny = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/rodzina.ics", "Rodzina");

        _db.CalendarSources.Add(rodzinny);
        _db.SaveChanges();

        var created = await _service.MoveEventAsync(_source.Id, "s1", rodzinny.Id);

        created.Should().NotBe("s1", "u Google to jest inne wydarzenie w innym kalendarzu");

        _writer.Wyslane.Select(w => w.Co).Should().Equal("utworzenie", "skasowanie");

        // U nas zostaje jeden wpis, w nowym kalendarzu.
        var onGrid = (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed;

        onGrid.Should().ContainSingle();
        onGrid.Single().Entry.SourceId.Should().Be(rodzinny.Id);
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
        _feed.Next = new FeedResult(
            [NewEvent("s1", "Zebranie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        var children = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dzieci", 0);
        children.SetColor("#FF8800", _hlc.Next());
        children.SetCalendar(_source.Id, _hlc.Next());
        _db.Areas.Add(children);
        _db.SaveChanges();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
            .Timed.Single().Entry.Color.Should().Be("#FF8800");

        // Zdjęcie przypisania oddaje wydarzenie barwie samego kalendarza — tam wracają
        // święta i wywiadówki, czyli wszystko, czego nikt do obszaru nie przypisał.
        children.SetCalendar(null, _hlc.Next());
        _db.SaveChanges();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0]
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
        var shell = new StructureEditService(
            new ProjectRepository(_db), new AreaRepository(_db), new TaskRepository(_db),
            new UnitOfWork(_db), _clock, _hlc);

        var children = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dzieci", 0);
        var dom = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 1);
        _db.Areas.AddRange(children, dom);
        _db.SaveChanges();

        await shell.SetAreaCalendarAsync(children.Id, _source.Id);
        _db.Areas.Single(o => o.Id == children.Id).CalendarId.Should().Be(_source.Id);

        // Ten sam kalendarz przy drugim obszarze zdejmuje go z pierwszego.
        await shell.SetAreaCalendarAsync(dom.Id, _source.Id);

        _db.Areas.Single(o => o.Id == dom.Id).CalendarId.Should().Be(_source.Id);
        _db.Areas.Single(o => o.Id == children.Id).CalendarId.Should().BeNull();
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
        var settings = new Settings { MainCalendarId = _source.Id };
        var mirror = new TaskMirror(
            _service, new TaskRepository(_db), new UnitOfWork(_db), _hlc, settings, new Notes(),
            new AreaRepository(_db));

        var rodzinny = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/rodzina.ics", "Rodzina");

        _db.CalendarSources.Add(rodzinny);

        var children = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dzieci", 0);
        children.SetCalendar(rodzinny.Id, _hlc.Next());
        _db.Areas.Add(children);

        var work = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Praca", 1);
        _db.Areas.Add(work);

        var odbior = TaskItem.Capture("Odebrać Sanię", _clock.Now, _hlc.Next());
        odbior.Schedule(children.Id, Today, _hlc.Next());
        odbior.SetDoTime(new TimeOnly(16, 0), _hlc.Next());
        _db.Tasks.Add(odbior);

        var report = TaskItem.Capture("Raport", _clock.Now, _hlc.Next());
        report.Schedule(work.Id, Today, _hlc.Next());
        report.SetDoTime(new TimeOnly(9, 0), _hlc.Next());
        _db.Tasks.Add(report);
        _db.SaveChanges();

        await mirror.PushAsync(_db.Tasks.Single(t => t.Id == odbior.Id));
        await mirror.PushAsync(_db.Tasks.Single(t => t.Id == report.Id));

        _db.Tasks.Single(t => t.Id == odbior.Id).SharedCalendarId
            .Should().Be(rodzinny.Id, "obszar Dzieci to kalendarz rodzinny");

        // Obszar bez własnego kalendarza spada do głównego — tam idą też wrzuty,
        // których jeszcze nikt nie rozstrzygnął.
        _db.Tasks.Single(t => t.Id == report.Id).SharedCalendarId.Should().Be(_source.Id);
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
        var settings = new Settings { MainCalendarId = _source.Id };
        var mirror = new TaskMirror(
            _service, new TaskRepository(_db), new UnitOfWork(_db), _hlc, settings, new Notes(),
            new AreaRepository(_db));

        // Drugi kalendarz zakładany tutaj, nie w konstruktorze: odświeżanie przechodzi
        // po **wszystkich** źródłach tym samym kanałem atrapy, więc stały drugi kalendarz
        // podwajałby wydarzenia w każdym innym teście w tym pliku.
        var wspolny = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/wspolny.ics", "Rodzina");

        _db.CalendarSources.Add(wspolny);

        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Wywiadówka", _clock.Now, _hlc.Next());
        task.Schedule(area.Id, Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(17, 0), _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        await mirror.PushAsync(_db.Tasks.Single(t => t.Id == task.Id));
        _db.Tasks.Single(t => t.Id == task.Id).SharedCalendarId.Should().Be(_source.Id);

        (await mirror.ShareAsync(task.Id, wspolny.Id)).Should().BeNull();

        // Najpierw znika ze starego, dopiero potem powstaje w nowym — inaczej przy
        // nieudanym zapisie zostałyby dwa wpisy na jedną rzecz.
        _writer.Wyslane.Select(w => w.Co).Should().ContainInOrder("skasowanie", "utworzenie");
        _db.Tasks.Single(t => t.Id == task.Id).SharedCalendarId.Should().Be(wspolny.Id);

        await mirror.UnshareAsync(task.Id);

        _db.Tasks.Single(t => t.Id == task.Id).SharedCalendarId
            .Should().Be(_source.Id, "cofnięcie wraca na główny, a nie znikąd");
        _writer.Wyslane[^1].Co.Should().Be("utworzenie");
    }

    [Fact]
    public async Task Zadanie_bez_dnia_wykonania_nie_trafia_na_siatke()
    {
        var area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Kiedyś", _clock.Now, _hlc.Next());
        task.MakeNext(area.Id, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        var day = (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0];

        day.Timed.Should().BeEmpty();
        day.AllDay.Should().BeEmpty();
    }

    [Fact]
    public async Task Wydarzenie_trwajace_przez_dzisiaj_pokazuje_sie_mimo_ze_zaczelo_sie_wczoraj()
    {
        // Warunek zachodzenia zakresów, nie „start w zakresie".
        _feed.Next = new FeedResult(
            [new FeedEvent(
                "a", "Wyjazd",
                new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.FromHours(2)),
                new DateTimeOffset(2026, 9, 18, 20, 0, 0, TimeSpan.FromHours(2)),
                false, null, false)],
            null, true);
        await _service.RefreshAsync();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1))[0].Timed.Should().ContainSingle();
    }

    [Fact]
    public async Task Kalendarz_da_sie_podlaczyc_i_odlaczyc()
    {
        // Do dziś nie było **żadnej** drogi, żeby dodać źródło: odświeżanie przechodziło
        // po kalendarzach, których nic nie umiało utworzyć, i kończyło się po cichu.
        var dodany = await _service.AddAsync(
            CalendarKind.Ical, "https://example.test/drugi.ics", "Zajęcia");

        (await _service.SourcesAsync()).Should().Contain(z => z.Id == dodany.Id);

        await _service.RemoveAsync(dodany.Id);

        (await _service.SourcesAsync()).Should().NotContain(z => z.Id == dodany.Id);

        // Nagrobek, nie usunięcie — wybór kalendarzy się synchronizuje (spec 5.1).
        _db.CalendarSources.Count(z => z.Id == dodany.Id).Should().Be(1);
    }

    [Fact]
    public async Task Nowe_wydarzenie_idzie_najpierw_do_zrodla_potem_do_bazy()
    {
        var start = new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.FromHours(2));

        var id = await _service.SaveEventAsync(
            _source.Id, externalId: null,
            new CalendarDraft("Dentysta", start, start.AddHours(1)));

        id.Should().Be("nowe-1");
        _writer.Wyslane.Should().ContainSingle(w => w.Co == "utworzenie" && w.Title == "Dentysta");

        var days = await _service.AgendaAsync(new DateOnly(2026, 9, 17), 1);
        days[0].Timed.Should().ContainSingle(s => s.Entry.Title == "Dentysta");
    }

    [Fact]
    public async Task Nieudany_zapis_u_zrodla_nie_zostawia_wydarzenia_u_nas()
    {
        // Kolejność jest tu treścią, nie szczegółem. Gdyby baza szła pierwsza,
        // nieudany zapis zostawiłby wydarzenie widoczne w Marshalu, a nieistniejące
        // w kalendarzu — czyli dokładnie to, przed czym zapis dwustronny ma chronić.
        _writer.Rzuca = true;
        var start = new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.FromHours(2));

        var patch = async () => await _service.SaveEventAsync(
            _source.Id, externalId: null,
            new CalendarDraft("Nie zapisze się", start, start.AddHours(1)));

        await patch.Should().ThrowAsync<HttpRequestException>();

        (await _service.AgendaAsync(new DateOnly(2026, 9, 17), 1))[0]
            .Timed.Should().BeEmpty();
    }

    [Fact]
    public async Task Kalendarz_bez_pisarza_mowi_ze_jest_do_odczytu()
    {
        var withoutWriter = new CalendarSyncService(
            _sklad, new TaskRepository(_db), [_feed], _clock, _hlc, new Settings(), [],
            new ProjectRepository(_db), new AreaRepository(_db));

        var start = new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.FromHours(2));

        var patch = async () => await withoutWriter.SaveEventAsync(
            _source.Id, null, new CalendarDraft("Cokolwiek", start, start.AddHours(1)));

        (await patch.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*tylko do odczytu*");
    }

    [Fact]
    public async Task Przeniesione_wydarzenie_da_sie_cofnac()
    {
        // Przeciągnięcie cudzego wydarzenia zapisuje się natychmiast w kalendarzu,
        // z którego pochodzi — bez pytania. „Cofnij" nie jest tu wygodą, tylko jedyną
        // odpowiedzią na omsknięcie ręki, więc ma odtwarzać **poprzednie** godziny.
        var start = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.FromHours(2));

        await _sklad.UpsertAsync(_source.Id,
            [new FeedEvent("w1", "Zebranie", start, start.AddHours(1), false, null, false)]);
        await _sklad.SaveChangesAsync();

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        var block = model.Columns.SelectMany(k => k.Slots).Single(b => b.Title == "Zebranie");

        await model.MoveEventAsync(block, new DateOnly(2026, 9, 18), 14 * 48);

        _writer.Wyslane.Should().Contain(w => w.Co == "zmiana" && w.Id == "w1");
        model.CanUndo.Should().BeTrue();

        await model.UndoMoveCommand.ExecuteAsync(null);

        model.CanUndo.Should().BeFalse();

        // Dwie zmiany u źródła: przeniesienie i powrót. Ostatnia przywraca stan sprzed.
        var ev = _db.CalendarEvents.Single(e => e.ExternalId == "w1");

        ev.StartsAt.Should().Be(start);
        ev.EndsAt.Should().Be(start.AddHours(1));
    }

    [Fact]
    public async Task Odhaczenie_zapisuje_blok_konczacy_sie_teraz()
    {
        // Zadanie bez godziny znikało po odhaczeniu z kalendarza bez śladu, więc
        // wieczorem nie było z czego odczytać, na co poszedł dzień. Blok zapisuje się
        // wstecz: kończy się teraz, zaczyna tyle wcześniej, ile miało trwać.
        _clock.Now = new DateTimeOffset(2026, 9, 17, 14, 37, 0, TimeSpan.FromHours(2));

        var task = TaskItem.Capture("Zadzwonić", _clock.Now, _hlc.Next());
        task.Schedule(Guid.CreateVersion7(), Today, _hlc.Next());
        task.SetEstimate(15, Energy.Unknown, _hlc.Next());
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        await _edit.CompleteAsync(task.Id);

        var po = _db.Tasks.Single(z => z.Id == task.Id);

        // 14:37 w dół do pięciu minut to 14:35, minus kwadrans daje 14:20.
        po.DoTime.Should().Be(new TimeOnly(14, 20));
        po.State.Should().Be(TaskState.Done);
    }

    [Fact]
    public async Task Odhaczenie_nie_rusza_godziny_wpisanej_wczesniej()
    {
        // Godzina wpisana wcześniej była decyzją, a nie zapisem tego, co się stało.
        _clock.Now = new DateTimeOffset(2026, 9, 17, 14, 37, 0, TimeSpan.FromHours(2));

        var task = TaskItem.Capture("Spotkanie", _clock.Now, _hlc.Next());
        task.Schedule(Guid.CreateVersion7(), Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(9, 0), _hlc.Next());
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        await _edit.CompleteAsync(task.Id);

        _db.Tasks.Single(z => z.Id == task.Id).DoTime.Should().Be(new TimeOnly(9, 0));
    }

    [Fact]
    public async Task Przeciagniecie_zmienia_dzien_i_godzine_a_nie_dlugosc()
    {
        var task = TaskItem.Capture("Przesunąć", _clock.Now, _hlc.Next());
        task.Schedule(Guid.CreateVersion7(), Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(9, 0), _hlc.Next());
        task.SetEstimate(90, Energy.Unknown, _hlc.Next());
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        // Jutro, czternasta — czyli 14 * 48 punktów od góry siatki.
        await model.MoveAsync(task.Id, Today.AddDays(1), 14 * 48);

        var po = _db.Tasks.Single(z => z.Id == task.Id);

        po.DoDate.Should().Be(Today.AddDays(1));
        po.DoTime.Should().Be(new TimeOnly(14, 0));
        po.EstimatedMinutes.Should().Be(90, "przeciągnięcie przesuwa, a nie skraca");
    }

    [Fact]
    public void Klikniecie_w_puste_miejsce_zaokragla_godzine_do_piatki_minut()
    {
        // Pięć minut, nie kwadrans: kwadrans był za grubą miarką na spotkanie o 9:35.
        // Minuta co do punktu byłaby za to udawaną precyzją — trafienie w 14:07 nie
        // znaczy, że ktoś planuje na 14:07.
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);

        (DateOnly Day, TimeOnly Time)? poproszono = null;
        model.NewTaskRequested += (day, time) => poproszono = (day, time);

        // Czternasta i siedem minut w punktach: 14 * 48 plus 5,6.
        model.NewAt(new DateOnly(2026, 9, 17), (14 * 48) + 5.6);

        poproszono.Should().NotBeNull();
        poproszono!.Value.Day.Should().Be(new DateOnly(2026, 9, 17));
        poproszono.Value.Time.Should().Be(new TimeOnly(14, 5));

        model.NewAt(new DateOnly(2026, 9, 17), (14 * 48) + 24);
        poproszono!.Value.Time.Should().Be(new TimeOnly(14, 30));

        // Koniec doby nie przekręca się na następny dzień.
        model.NewAt(new DateOnly(2026, 9, 17), 24 * 48);
        poproszono!.Value.Time.Should().Be(new TimeOnly(23, 55));
    }

    [Fact]
    public void Klikniete_wydarzenie_mowi_czym_jest_zamiast_milczec()
    {
        // Wydarzenia z cudzego kalendarza nie da się tu zmienić i to jest zamierzone.
        // Ale przycisk, który po kliknięciu nie robi nic, wygląda jak zepsuty — a nie
        // jak granica, która ma powód.
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);

        var ev = new SlotBox(
            "Zebranie", 0, 48, 0, 200, IsTask: false, Color: null,
            "10:00", "11:00", TaskId: null, "17.09.2026",
            SourceId: null, ExternalId: null, IsDone: false);

        model.OpenTaskCommand.Execute(ev);

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
        var day = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

        await _sklad.UpsertAsync(_source.Id,
        [
            new FeedEvent("ksiezyc", "Pierwsza kwadra", day, day.AddDays(1),
                IsAllDay: true, null, false),
        ]);
        await _sklad.SaveChangesAsync();

        var days = await _service.AgendaAsync(new DateOnly(2026, 9, 18), 2);

        days[0].AllDay.Should().ContainSingle(e => e.Title == "Pierwsza kwadra");
        days[1].AllDay.Should().BeEmpty("to jest jeden dzień, a nie dwa");
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

        await _sklad.UpsertAsync(_source.Id,
        [
            new FeedEvent("lato", "Lipiec", lato, lato.AddHours(1), false, null, false),
            new FeedEvent("zima", "Grudzień", zima, zima.AddHours(1), false, null, false),
        ]);
        await _sklad.SaveChangesAsync();

        var wLipcu = await _service.AgendaAsync(new DateOnly(2026, 7, 15), 1);
        var wGrudniu = await _service.AgendaAsync(new DateOnly(2026, 12, 15), 1);

        wLipcu[0].Timed.Single().Entry.Start.Hour.Should().Be(14, "w lipcu Polska ma +2");
        wGrudniu[0].Timed.Single().Entry.Start.Hour.Should().Be(13, "w grudniu +1");
    }

    [Fact]
    public async Task Szerokosc_kolumny_idzie_za_oknem()
    {
        // Stała szerokość na widok zostawiała dwie trzecie pustego miejsca obok siatki
        // na monitorze i kazała przewijać w bok w wąskim oknie. Bloki liczą się od
        // szerokości kolumny, więc muszą się przeliczyć razem z nią.
        _feed.Next = new FeedResult(
            [NewEvent("s1", "Spotkanie", "2026-09-16", 10, 11)], SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
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
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
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
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        await model.ShowWeekCommand.ExecuteAsync(null);
        model.SelectedRange!.Days.Should().Be(7);

        await model.ShowMonthCommand.ExecuteAsync(null);
        model.SelectedRange!.Month.Should().BeTrue();

        // Dotknięcie dnia w miesiącu schodzi na jego siatkę godzinową.
        await model.OpenMonthDayCommand.ExecuteAsync(
            new MonthCell(new DateOnly(2026, 9, 16), "16", true, false, [], 0));

        model.SelectedRange!.Month.Should().BeFalse();
        model.SelectedRange!.Days.Should().Be(1);
    }

    [Fact]
    public async Task Kreska_teraz_stoi_tylko_na_dzisiejszej_kolumnie()
    {
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        var dzisiejsze = model.Columns.Where(k => k.IsToday).ToList();

        dzisiejsze.Should().ContainSingle("dzisiaj jest jedno");
        dzisiejsze[0].Date.Should().Be(Today);

        // Zegar testu stoi na 9:00, godzina ma 48 punktów.
        dzisiejsze[0].NowTop.Should().Be(9 * 48);
    }

    [Fact]
    public async Task Odhaczenie_z_siatki_zamyka_zadanie()
    {
        // Odhaczenie ma iść tą samą drogą co z ekranu szczegółu — przez usługę edycji,
        // a nie przez wyrzucenie bloku z siatki. Blok znikający bez zapisu wyglądałby
        // identycznie i wracałby przy następnym odświeżeniu.
        var task = TaskItem.Capture("Zadzwonić", _clock.Now, _hlc.Next());
        task.Schedule(Guid.CreateVersion7(), Today, _hlc.Next());
        task.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        var block = model.Columns.SelectMany(k => k.Slots).Single(b => b.Title == "Zadzwonić");
        block.CanComplete.Should().BeTrue("zadanie da się odhaczyć, cudze wydarzenie nie");

        await model.CompleteCommand.ExecuteAsync(block.TaskId);

        _db.Tasks.Single(z => z.Id == task.Id).State.Should().Be(TaskState.Done);
    }

    [Fact]
    public async Task Siatka_otwiera_sie_na_biezacej_godzinie()
    {
        // Doba ma 1152 punkty, a ekran telefonu mieści z tego jakąś jedną czwartą:
        // otwarcie o północy pokazuje godziny, w których się śpi, i za każdym razem
        // zaczyna się od przewijania. Godzina zapasu u góry, stąd nie 9 * 48, a 8 * 48.
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);

        double? to = null;
        model.ScrollRequested += score => to = score;

        await model.LoadAsync();

        to.Should().Be(8 * 48);
    }

    [Fact]
    public async Task Dni_bez_dzisiaj_nie_sa_przewijane()
    {
        // Godzina z innego dnia nie jest odpowiedzią na nic, a skok kasowałby
        // pozycję, którą użytkownik ustawił ręką przed chwilą.
        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);

        await model.LoadAsync();

        double? to = null;
        model.ScrollRequested += score => to = score;

        await model.NextCommand.ExecuteAsync(null);
        await model.LoadAsync();

        to.Should().BeNull();
    }

    [Fact]
    public async Task Pusta_siatka_przy_pelnej_bazie_trafia_do_dziennika()
    {
        // Ten dokładnie przypadek zżarł jedną rundę: 949 wydarzeń w bazie, osiem na
        // siatce, zero narysowanych — i trzy różne możliwe przyczyny wyglądające
        // identycznie. Wpis zapisuje się **tylko** wtedy, bo przy każdym przerysowaniu
        // zalałby dziennik tym, co i tak widać na ekranie.
        _feed.Next = new FeedResult(
            [NewEvent("d1", "Dawno temu", "2026-01-05", 10, 11)], SyncToken: null, IsFull: true);

        await _service.RefreshAsync(force: true);

        var notes = new Notes();
        var model = new CalendarViewModel(_service, _clock, notes, _edit);

        await model.LoadAsync();

        notes.Entries.Should().Contain(w =>
            w.Operation == "Kalendarz: siatka" && w.Level == ActivityLevel.Problem);
    }

    [Fact]
    public async Task Barwa_dociaga_sie_przy_pobraniu()
    {
        // Kalendarze podłączone przed wprowadzeniem barw mają w bazie pusto i bez
        // tego zostałyby szare na zawsze — a jedyną drogą byłoby odłączenie ich
        // i dodanie od nowa, czyli naprawianie ręką czegoś, co aplikacja wie.
        _feed.Next = new FeedResult([], SyncToken: null, IsFull: true, Color: "#8E24AA");

        await _service.RefreshAsync(force: true);

        (await _service.SourcesAsync())
            .Single(z => z.Id == _source.Id).Color.Should().Be("#8E24AA");
    }

    [Fact]
    public async Task Brak_barwy_u_zrodla_nie_kasuje_zapisanej()
    {
        // „Nie wiem" i „bez koloru" to dwie różne odpowiedzi. Kanał, który barwy nie
        // podaje, nie ma podstaw, żeby czyścić tę, którą już mamy.
        _feed.Next = new FeedResult([], SyncToken: null, IsFull: true, Color: "#8E24AA");
        await _service.RefreshAsync(force: true);

        _feed.Next = new FeedResult([], SyncToken: null, IsFull: true);
        await _service.RefreshAsync(force: true);

        (await _service.SourcesAsync())
            .Single(z => z.Id == _source.Id).Color.Should().Be("#8E24AA");
    }

    [Fact]
    public async Task Wydarzenie_dostaje_barwe_swojego_kalendarza()
    {
        // Barwa siedzi na kalendarzu, a rysuje się wydarzenie — i to jest jedyna
        // rzecz, po której przy jedenastu podłączonych kalendarzach widać, do
        // którego z nich coś należy. Droga wiedzie przez cztery warstwy, więc
        // urwana po cichu w dowolnej z nich wygląda jak „wszystko jest szare".
        var kolorowy = await _service.AddAsync(
            CalendarKind.Ical, "https://example.test/praca.ics", "Praca", "#8E24AA");

        await _sklad.UpsertAsync(
            kolorowy.Id, [NewEvent("p1", "Spotkanie", "2026-09-16", 10, 11)]);
        await _sklad.SaveChangesAsync();

        var days = await _service.AgendaAsync(new DateOnly(2026, 9, 16), 1);

        days[0].Timed.Should().ContainSingle(s => s.Entry.Title == "Spotkanie")
            .Which.Entry.Color.Should().Be("#8E24AA");
    }

    [Fact]
    public async Task Ten_sam_kalendarz_dodany_dwa_razy_zostaje_jednym()
    {
        // Klikanie „Dodaj" w reakcji na to, że nic się nie pojawiło, jest odruchem —
        // a duplikaty mnożą potem te same błędy w raporcie i zaciemniają ten jeden,
        // który coś znaczy.
        var first = await _service.AddAsync(
            CalendarKind.Google, "primary", "Mój kalendarz");

        var drugi = await _service.AddAsync(
            CalendarKind.Google, "primary", "Mój kalendarz jeszcze raz");

        drugi.Id.Should().Be(first.Id);
        (await _service.SourcesAsync()).Count(z => z.ExternalId == "primary").Should().Be(1);
    }

    [Fact]
    public async Task Calodniowe_zostaje_calodniowym_takze_w_naszej_kopii()
    {
        // Nasza kopia zapisywała każde wydarzenie jako godzinowe, niezależnie od tego,
        // czym było. Do Google jechało przy tym poprawnie, jako data bez godziny —
        // rozjeżdżała się wyłącznie kopia, i najgorszym możliwym sposobem: przy każdym
        // odświeżeniu wracała prawidłowa, a przy każdym zapisie psuła się z powrotem.
        //
        // Objaw na ekranie: granice całodniowego to północ bez strefy, więc narysowane
        // jako godzinowe w Warszawie dają bloczek od drugiej w nocy do drugiej w nocy
        // **następnego dnia** — jeden wpis rozlany na dwie doby.
        var day = new DateOnly(2026, 9, 18);

        await _service.SaveEventAsync(
            _source.Id,
            externalId: null,
            new CalendarDraft(
                "Pierwsza kwadra",
                new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                Location: null,
                AllDay: true));

        _db.CalendarEvents.Single(e => e.Title == "Pierwsza kwadra")
            .IsAllDay.Should().BeTrue();

        // I to samo od strony siatki: wpis całodniowy stoi na pasku jednego dnia,
        // a nie jako bloczek godzinowy rozciągnięty na dwa.
        var days = await _service.AgendaAsync(day, 2);

        days[0].AllDay.Should().ContainSingle(w => w.Title == "Pierwsza kwadra");
        days[0].Timed.Should().NotContain(s => s.Entry.Title == "Pierwsza kwadra");
        days[1].Timed.Should().NotContain(s => s.Entry.Title == "Pierwsza kwadra");
    }

    [Fact]
    public async Task Kalendarz_tylko_do_odczytu_odmawia_przed_czynnoscia_a_nie_po_niej()
    {
        // Odmowa po fakcie przy przenoszeniu wydarzenia znaczy kopię założoną w nowym
        // kalendarzu, zanim odmowa przyszła ze starego. Poziom dostępu Google znamy
        // z listy kalendarzy, więc da się odpowiedzieć, zanim cokolwiek się wydarzy.
        _source.SetReadOnly(true);
        await _db.SaveChangesAsync();

        var proba = async () => await _service.SaveEventAsync(
            _source.Id,
            externalId: null,
            new CalendarDraft("Cokolwiek", _clock.Now, _clock.Now.AddHours(1)));

        (await proba.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*tylko do odczytu*");

        // Nic nie poszło do źródła — o to właśnie chodzi w słowie „przed".
        _writer.Wyslane.Should().BeEmpty();
    }

    [Fact]
    public async Task Wydarzenie_z_kalendarza_do_odczytu_nie_ma_na_siatce_pola_wyboru()
    {
        // Pole wyboru przy wydarzeniu, którego nie da się odhaczyć, uczy nieufności
        // do wszystkich pozostałych. To samo rozstrzyga o polu „Obszar" na karcie:
        // czego nie wolno zmienić, tego się nie proponuje.
        _source.SetReadOnly(true);
        await _db.SaveChangesAsync();

        _feed.Next = new FeedResult(
            [NewEvent("a", "Pierwsza kwadra", "2026-09-18", 8, 9)], null, true);

        await _service.RefreshAsync(force: true);

        (await _service.AgendaAsync(new DateOnly(2026, 9, 18), 1))[0]
            .Timed.Single().Entry.CanWrite.Should().BeFalse();
    }

    [Fact]
    public async Task Karta_wydarzenia_do_odczytu_mowi_o_tym_zamiast_proponowac_zapis()
    {
        // Zdanie „tylko do odczytu" stało na karcie od początku, ale pokazywało się po
        // pytaniu o **rodzaj** kalendarza — „czy umiemy pisać do Google". Dla kalendarza
        // świątecznego odpowiedź brzmiała „tak", więc zdania nie było widać, a przyciski
        // „Zapisz" i „Skasuj" owszem. Odmowa przychodziła dopiero po naciśnięciu.
        _source.SetReadOnly(true);
        await _db.SaveChangesAsync();

        _feed.Next = new FeedResult(
            [NewEvent("a", "Pierwsza kwadra", "2026-09-16", 8, 9)], null, true);

        await _service.RefreshAsync(force: true);

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        var block = model.Columns
            .SelectMany(k => k.Slots)
            .Single(b => b.Title == "Pierwsza kwadra");

        model.OpenTaskCommand.Execute(block);

        model.HasOpened.Should().BeTrue();
        model.CanEditOpened.Should().BeFalse(
            "kalendarz jest tylko do odczytu, więc karta ma to powiedzieć, a nie proponować zapis");
    }

    [Fact]
    public async Task Karta_wydarzenia_z_wlasnego_kalendarza_dalej_pozwala_zapisac()
    {
        // Druga strona tego samego: poprawka miała zdjąć przyciski tam, gdzie zapis nie
        // ma dokąd pójść, a nie wszędzie.
        _feed.Next = new FeedResult(
            [NewEvent("b", "Zebranie", "2026-09-16", 10, 11)], null, true);

        await _service.RefreshAsync(force: true);

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();

        var block = model.Columns
            .SelectMany(k => k.Slots)
            .Single(b => b.Title == "Zebranie");

        model.OpenTaskCommand.Execute(block);

        model.CanEditOpened.Should().BeTrue();
    }

    [Fact]
    public async Task Poziom_dostepu_odswieza_sie_przy_kazdym_pobraniu()
    {
        // Dostęp się zmienia: ktoś dopuszcza do swojego kalendarza albo odbiera dostęp.
        // Aplikacja, która pyta o to raz przy podłączaniu, myli się od tamtej chwili
        // do końca.
        _feed.Next = new FeedResult([], null, true, Color: null, ReadOnly: true);
        await _service.RefreshAsync(force: true);

        (await _service.SourcesAsync()).Single(z => z.Id == _source.Id)
            .ReadOnly.Should().BeTrue();

        _clock.Now = _clock.Now.Add(CalendarSyncService.RefreshInterval).AddMinutes(1);
        _feed.Next = new FeedResult([], null, true, Color: null, ReadOnly: false);
        await _service.RefreshAsync(force: true);

        (await _service.SourcesAsync()).Single(z => z.Id == _source.Id)
            .ReadOnly.Should().BeFalse("dostęp wrócił, więc pole „Obszar” ma znów być czynne");
    }

    [Fact]
    public async Task Zrodlo_bez_zdania_o_dostepie_nie_nadpisuje_tego_co_wiemy()
    {
        // Kanał iCal nie zna pojęcia poziomu dostępu. Puste znaczy „nie mówię", a nie
        // „wolno pisać" — inaczej pierwsze pobranie z kanału kasowałoby odpowiedź,
        // którą podało Google.
        _source.SetReadOnly(true);
        await _db.SaveChangesAsync();

        _feed.Next = new FeedResult([], null, true);
        await _service.RefreshAsync(force: true);

        (await _service.SourcesAsync()).Single(z => z.Id == _source.Id)
            .ReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Nieudane_zdjecie_ze_starego_kalendarza_nie_zostawia_kopii_w_nowym()
    {
        // Przenoszenie wydarzenia u Google to założenie w nowym i skasowanie w starym.
        // Przy kalendarzu tylko do odczytu — świątecznym, fazach księżyca, cudzym bez
        // prawa zmian — pierwsze się udaje, drugie wraca odmową. Zostawała odmowa
        // na ekranie **i** kopia, o którą nikt nie prosił.
        _feed.Next = new FeedResult(
            [NewEvent("ksiezyc", "Pierwsza kwadra", "2026-09-18", 8, 9)], null, true);

        await _service.RefreshAsync(force: true);

        // Kalendarz obszaru zakładany **po** pobraniu: atrapa kanału oddaje tę samą
        // listę każdemu podłączeniu, więc założony wcześniej dostałby kopię wydarzenia
        // z pobrania i test mówiłby o duplikacie, którego nie badamy.
        var target = new CalendarSource(
            Guid.CreateVersion7(), _clock.Now, _hlc.Next(),
            CalendarKind.Ical, "https://example.test/moj.ics", "Rozwój własny");

        _db.CalendarSources.Add(target);
        await _db.SaveChangesAsync();

        // Stary kalendarz przestaje przyjmować zapisy — tak jak świąteczny u Google.
        _writer.ReadOnly.Add(_source.Id);

        var proba = async () => await _service.MoveEventAsync(_source.Id, "ksiezyc", target.Id);

        await proba.Should().ThrowAsync<InvalidOperationException>();

        // Kopia zdjęta u źródła…
        _writer.Wyslane.Should().Contain(w => w.Co == "skasowanie" && w.Id == "nowe-1");

        var day = (await _service.AgendaAsync(new DateOnly(2026, 9, 18), 1))[0];

        // …i u nas. To jest cała treść poprawki: po odmowie nie zostaje nic, o co nikt
        // nie prosił.
        day.Timed.Should().NotContain(
            s => s.Entry.ExternalId == "nowe-1",
            "kopia w nowym kalendarzu ma zniknąć razem z nieudanym przeniesieniem");

        // A wydarzenie u źródła zostaje nietknięte — bo właśnie nie dało się go zdjąć.
        day.Timed.Should().Contain(s => s.Entry.ExternalId == "ksiezyc");
    }

    [Fact]
    public async Task Ten_sam_kalendarz_z_dwoch_kont_to_dwa_podlaczenia()
    {
        // Kalendarz udostępniony obu stronom widnieje u każdej pod tym samym adresem.
        // Gdyby konto nie liczyło się do rozpoznania duplikatu, drugie podłączenie
        // po cichu oddawałoby pierwsze — czyli kalendarz służbowy czytany byłby
        // żetonem konta prywatnego, które nie ma do niego prawa.
        var work = await _service.AddAsync(
            CalendarKind.Google, "wspolny@group.calendar.google.com", "Wspólny",
            account: "praca@example.test");

        var dom = await _service.AddAsync(
            CalendarKind.Google, "wspolny@group.calendar.google.com", "Wspólny",
            account: "dom@example.test");

        dom.Id.Should().NotBe(work.Id);

        var sources = await _service.SourcesAsync();
        sources.Where(z => z.ExternalId == "wspolny@group.calendar.google.com")
            .Select(z => z.Account)
            .Should().BeEquivalentTo(["praca@example.test", "dom@example.test"]);
    }

    [Fact]
    public async Task Konto_glowne_i_dodatkowe_to_nie_jest_ten_sam_kalendarz()
    {
        // Ta sama para rodzaj–identyfikator, różne konta: składanie duplikatów przy
        // odświeżaniu ma zostawić oba. Objaw pomyłki byłby cichy — jeden z dwóch
        // kalendarzy przestałby się pobierać, bez śladu na ekranie.
        await _service.AddAsync(CalendarKind.Google, "primary", "Mój");
        await _service.AddAsync(
            CalendarKind.Google, "primary", "Służbowy", account: "praca@example.test");

        var report = await _service.RefreshAsync(force: true);

        report.Folded.Should().Be(0);
        (await _service.SourcesAsync()).Count(z => z.ExternalId == "primary").Should().Be(2);
    }

    [Fact]
    public async Task Komorka_miesiaca_miesci_tyle_wpisow_ile_ma_wysokosci()
    {
        // Stała czwórka była nietrafiona w obie strony: na komputerze chowała za
        // licznikiem rzeczy, na które było miejsce, a na telefonie przy sześciu
        // tygodniach czwarta linijka i tak się nie mieściła — więc licznik mówił „+1",
        // gdy niewidoczne były dwie.
        _feed.Next = new FeedResult(
            [
                NewEvent("a", "Pierwsze", "2026-09-17", 8, 9),
                NewEvent("b", "Drugie", "2026-09-17", 10, 11),
                NewEvent("c", "Trzecie", "2026-09-17", 12, 13),
                NewEvent("d", "Czwarte", "2026-09-17", 14, 15),
                NewEvent("e", "Piąte", "2026-09-17", 16, 17),
                NewEvent("f", "Szóste", "2026-09-17", 18, 19),
            ],
            null,
            true);

        await _service.RefreshAsync();

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();
        await model.ShowMonthCommand.ExecuteAsync(null);

        // Siatka na tyle wysoka, że w komórce mieści się wszystko sześć.
        model.SetMonthHeight(1200);

        var day = NewCell(model, new DateOnly(2026, 9, 17));
        day.Entries.Should().HaveCount(6);
        day.Overflow.Should().Be(0);

        // Ta sama siatka na telefonie: mieści mniej i mówi, ile schowała.
        model.SetMonthHeight(420);

        var ciasno = NewCell(model, new DateOnly(2026, 9, 17));
        ciasno.Entries.Should().HaveCountLessThan(6);
        ciasno.Overflow.Should().Be(6 - ciasno.Entries.Count);
    }

    [Fact]
    public async Task Komorka_pokazuje_zawsze_choc_jeden_wpis()
    {
        // Komórka, w której nie widać niczego poza liczbą, przestaje mówić cokolwiek
        // o tym, czym dzień jest zajęty — a po to się na miesiąc patrzy.
        _feed.Next = new FeedResult(
            [NewEvent("a", "Jedyne", "2026-09-17", 8, 9)], null, true);

        await _service.RefreshAsync();

        var model = new CalendarViewModel(_service, _clock, new Notes(), _edit);
        await model.LoadAsync();
        await model.ShowMonthCommand.ExecuteAsync(null);

        // Wysokość, przy której nie mieści się nawet jeden wiersz.
        model.SetMonthHeight(60);

        NewCell(model, new DateOnly(2026, 9, 17)).Entries.Should().ContainSingle();
    }

    private static MonthCell NewCell(CalendarViewModel model, DateOnly day) =>
        model.MonthWeeks.SelectMany(t => t.Cells).Single(k => k.Date == day);

    [Fact]
    public void Kwadracik_na_siatce_jest_na_pulpicie_i_nie_ma_go_na_dotyku()
    {
        // Progi wysokości i szerokości mówią, czy kwadracik **się zmieści**. Na telefonie
        // pytanie brzmi inaczej: czy da się w niego trafić. Blok kwadransa ma kilkanaście
        // punktów, więc kwadracik wychodzi mniejszy od opuszka i leży na czymś, co
        // równocześnie przeciąga się i otwiera. Na pulpicie odsłania się przy najechaniu
        // i trafia się w niego kursorem co do punktu.
        var block = new SlotBox(
            "Zadanie", 0, 30, 0, 100, true, null, "09:00", "10:00",
            Guid.CreateVersion7(), "śr", null, null, false);

        var before = Platform.Touch;

        try
        {
            Platform.Touch = false;
            block.ShowCheck.Should().BeTrue("na pulpicie kwadracik zostaje");

            Platform.Touch = true;
            block.ShowCheck.Should().BeFalse("palec nie trafia w kwadracik wielkości dziesięciu punktów");

            // Ptaszek odhaczonego wpisu zostaje wszędzie: to jest stan, nie przycisk.
            (block with { IsDone = true }).ShowMarkColumn.Should().BeTrue();
        }
        finally
        {
            Platform.Touch = before;
        }
    }

    [Fact]
    public void Wpis_miesiaca_stoi_na_tle_w_barwie_swojego_obszaru()
    {
        // Miesiąc czyta się wzrokiem, nie literami: cztery jednakowe linijki w komórce
        // wyglądają tak samo niezależnie od tego, czy to cztery rzeczy z pracy, czy trzy
        // przedszkolne i jedna urzędowa. Barwa obszaru odpowiada na to bez czytania.
        var work = new MonthEntry("09:00", "Gala", null, false, "#C0392B");
        var dom = new MonthEntry("17:00", "Przedszkole", null, false, "#27AE60");

        var pierwsza = ((SolidColorBrush)work.Background).Color;
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
        const string color = "#8E24AA";

        var block = new SlotBox(
            "Spotkanie", 0, 30, 0, 100, false, color, "09:00", "10:00", null, "śr", null, null, false);

        var entry = new MonthEntry("09:00", "Spotkanie", null, false, color);

        var onGrid = ((SolidColorBrush)block.Background).Color;
        var inCell = ((SolidColorBrush)entry.Background).Color;

        (inCell.R, inCell.G, inCell.B).Should().Be((onGrid.R, onGrid.G, onGrid.B));
        inCell.A.Should().BeLessThan(onGrid.A);
    }

    [Fact]
    public void Barwa_nie_do_odczytania_nie_robi_z_wpisu_wyroznionego()
    {
        // Obszar bez barwy i barwa zapisana czymś, czego nie umiemy odczytać, mają
        // wyglądać jak wszystko inne bez barwy. Powrót do krycia pełnego dawał jedyny
        // nieprzezroczysty prostokąt na siatce — czyli wpis wyróżniony za to, że coś
        // z nim nie tak.
        var withoutColor = new MonthEntry("09:00", "Bez barwy", null, false, null);
        var zeSmieciem = new MonthEntry("09:00", "Ze śmieciem", null, false, "obszar Praca");

        ((SolidColorBrush)zeSmieciem.Background).Color
            .Should().Be(((SolidColorBrush)withoutColor.Background).Color);
    }

    [Fact]
    public async Task Nieudany_kanal_mowi_dlaczego_a_nie_tylko_ze()
    {
        // Sama liczba porażek wygląda tak samo przy braku zgody, złym adresie i padniętej
        // sieci. Powód jest jedyną rzeczą, z której da się coś zrobić.
        _feed.Rzuca = true;

        var report = await _service.RefreshAsync(force: true);

        report.Failed.Should().Be(1);
        report.Problems.Should().ContainSingle()
            .Which.Should().Contain("Przedszkole").And.Contain("kanał nie odpowiada");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
