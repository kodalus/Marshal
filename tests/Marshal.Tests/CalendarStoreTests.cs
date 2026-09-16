using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Areas;
using Marshal.Domain.Calendar;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Calendar;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
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

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Zegar _zegar = new();
    private readonly HlcSource _hlc;
    private readonly CalendarStore _sklad;
    private readonly Kanal _kanal = new(CalendarKind.Ical);
    private readonly CalendarSyncService _usluga;
    private readonly CalendarSource _zrodlo;

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

        _usluga = new CalendarSyncService(_sklad, new TaskRepository(_db), [_kanal], _zegar);

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

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}
