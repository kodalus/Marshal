using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Areas;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Backup;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Codzienna kopia zapasowa: raz na dobę, bez zadania w tle.
/// </summary>
/// <remarks>
/// Kopia zapasowa jest jedyną rzeczą w aplikacji, o której nikt się nie dowie, że nie
/// działa, dopóki nie będzie za późno. Dlatego testowana jest także po stronie odmowy:
/// wyłączona ma milczeć, a folder, do którego nie da się pisać, ma zostawić wpis
/// w dzienniku zamiast wyjątku.
/// </remarks>
public sealed class DailyBackupTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private sealed class StaleId : IDeviceIdentity
    {
        public string Id => "biurko";
    }

    private sealed class Settings(string folder) : ISettings
    {
        public bool DailyBackup { get; set; } = true;

        public string? BackupFolder { get; set; } = folder;

        public void SetDailyBackup(bool on) => DailyBackup = on;

        public void SetBackupFolder(string? path) => BackupFolder = path;

        public TimeZoneInfo Zone => TimeZoneInfo.Utc;

        public string? ZoneProblem => null;

        public ThemeChoice Theme => ThemeChoice.System;

        public string? GoogleClientId => null;

        public string? GoogleClientSecret => null;

        public bool GoogleCalendarEnabled => false;

        public Guid? MainCalendarId => null;

        public IReadOnlyList<string> CalendarAccounts => [];

        public void SetMainCalendar(Guid? calendarId) => throw new NotSupportedException();

        public void AddCalendarAccount(string email) => throw new NotSupportedException();

        public void RemoveCalendarAccount(string email) => throw new NotSupportedException();

        public void SetGoogleCalendarEnabled(bool enabled) => throw new NotSupportedException();

        public void SetZone(string id) => throw new NotSupportedException();

        public void SetTheme(ThemeChoice theme) => throw new NotSupportedException();

        public void SetGoogle(string? clientId, string? clientSecret) =>
            throw new NotSupportedException();
    }

    private sealed class Journal : IActivityLog
    {
        public List<(string What, string Outcome, ActivityLevel Level)> Lines { get; } = [];

        public int Dropped => 0;

        public Task RecordAsync(
            string operation, string outcome, ActivityLevel level = ActivityLevel.Ok,
            string? detail = null, CancellationToken ct = default)
        {
            Lines.Add((operation, outcome, level));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEntry>> RecentAsync(
            int count = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActivityEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly Journal _journal = new();
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"marshal-kopie-{Guid.CreateVersion7():N}");

    public DailyBackupTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();

        var hlc = new HlcSource(_clock, "biurko");
        var area = new Area(Guid.CreateVersion7(), _clock.Now, hlc.Next(), "Dom", sortOrder: 0);
        _db.Areas.Add(area);

        var task = TaskItem.Capture("Wynieść śmieci", _clock.Now, hlc.Next());
        task.MakeNext(area.Id, hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();
    }

    private (DailyBackup Copy, Settings Set) Build()
    {
        var settings = new Settings(_folder);
        var hlc = new HlcSource(_clock, "biurko");
        var backup = new BackupService(_db, hlc, _clock, new StaleId());

        return (new DailyBackup(backup, settings, _clock, _journal), settings);
    }

    private string[] Files() => Directory.Exists(_folder)
        ? [.. Directory.GetFiles(_folder).Select(Path.GetFileName).OfType<string>().Order()]
        : [];

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Pierwsze_uruchomienie_zaklada_folder_i_kopie_z_dzisiejsza_data()
    {
        var (copy, _) = Build();

        await copy.RunAsync();

        Files().Should().Equal("marshal-2026-09-22.json");
        copy.Last().Should().Be(new DateOnly(2026, 9, 22));

        // Plik ma nieść dane, nie samą nazwę: pusta kopia z poprawną nazwą jest
        // dokładnie tym rodzajem cichej porażki, którą kopia ma wykluczać.
        var written = await File.ReadAllTextAsync(Path.Combine(_folder, "marshal-2026-09-22.json"));
        written.Should().Contain("Wynieść śmieci");

        _journal.Lines.Should().ContainSingle(w => w.Level == ActivityLevel.Ok);
    }

    [Fact]
    public async Task Drugie_wolanie_tego_samego_dnia_nie_robi_niczego()
    {
        var (copy, _) = Build();

        await copy.RunAsync();

        var where = Path.Combine(_folder, "marshal-2026-09-22.json");
        var first = File.GetLastWriteTimeUtc(where);

        await copy.RunAsync();

        Files().Should().HaveCount(1);
        File.GetLastWriteTimeUtc(where)
            .Should().Be(first, "kopia na dziś już była, więc nie ma czego nadpisywać");
    }

    [Fact]
    public async Task Nowy_dzien_doklada_kopie_obok_wczorajszej()
    {
        var (copy, _) = Build();

        await copy.RunAsync();

        _clock.Now = _clock.Now.AddDays(1);
        await copy.RunAsync();

        Files().Should().Equal("marshal-2026-09-22.json", "marshal-2026-09-23.json");
        copy.Last().Should().Be(new DateOnly(2026, 9, 23));
    }

    [Fact]
    public async Task Ponad_trzydziesci_kopii_kasuje_najstarsze()
    {
        Directory.CreateDirectory(_folder);

        // Trzydzieści pięć dni wstecz, licząc od wczoraj: dzisiejsza dopiero powstanie.
        for (var back = 1; back <= 35; back++)
        {
            var day = DateOnly.FromDateTime(_clock.Now.Date).AddDays(-back);
            await File.WriteAllTextAsync(
                Path.Combine(_folder, $"marshal-{day:yyyy-MM-dd}.json"), "{}");
        }

        // Plik o obcej nazwie ma zostać nietknięty: sprzątamy po sobie, nie po folderze.
        await File.WriteAllTextAsync(Path.Combine(_folder, "notatki.json"), "{}");

        var (copy, _) = Build();
        await copy.RunAsync();

        var left = Files();

        left.Should().HaveCount(DailyBackup.Keep + 1, "trzydzieści kopii i jeden obcy plik");
        left.Should().Contain("notatki.json");
        left.Should().Contain("marshal-2026-09-22.json", "dzisiejsza jest najnowsza");
        left.Should().NotContain("marshal-2026-08-18.json", "najstarsze poszły");
    }

    [Fact]
    public async Task Kopie_do_przywrocenia_ida_od_najnowszej_i_daja_sie_otworzyc()
    {
        var (copy, _) = Build();

        await copy.RunAsync();

        _clock.Now = _clock.Now.AddDays(1);
        await copy.RunAsync();

        copy.Days().Should().Equal(new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 22));

        await using var stream = copy.Open(new DateOnly(2026, 9, 22));
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Contain("Wynieść śmieci");
    }

    [Fact]
    public async Task Kopia_sprzed_przywrocenia_powstaje_i_nie_wchodzi_na_liste_dni()
    {
        var (copy, _) = Build();

        await copy.RunAsync();

        var name = await copy.SafetyAsync();

        name.Should().StartWith("marshal-przed-przywroceniem-");
        File.Exists(Path.Combine(_folder, name)).Should().BeTrue();

        // Nie jest kopią dnia i ma się nią nie stać: inaczej przywracanie proponowałoby
        // ją jako dzień, a sprzątanie liczyłoby ją do trzydziestu.
        copy.Days().Should().Equal(new DateOnly(2026, 9, 22));
        copy.Last().Should().Be(new DateOnly(2026, 9, 22));
    }

    [Fact]
    public async Task Sprzatanie_nie_rusza_kopii_sprzed_przywrocenia()
    {
        Directory.CreateDirectory(_folder);

        for (var back = 1; back <= 35; back++)
        {
            var day = DateOnly.FromDateTime(_clock.Now.Date).AddDays(-back);
            await File.WriteAllTextAsync(
                Path.Combine(_folder, $"marshal-{day:yyyy-MM-dd}.json"), "{}");
        }

        var (copy, _) = Build();
        var safety = await copy.SafetyAsync();

        await copy.RunAsync();

        // Ta jedna przeżywa wszystko: powstaje raz na parę lat i wtedy, gdy właśnie
        // stało się coś złego. Skasowana przez sprzątanie byłaby siatką, która znika
        // dokładnie w chwili skoku.
        File.Exists(Path.Combine(_folder, safety))
            .Should().BeTrue("kopia sprzed przywrócenia nie podlega sprzątaniu");
    }

    [Fact]
    public async Task Wylaczona_kopia_nie_zaklada_nawet_folderu()
    {
        var (copy, settings) = Build();
        settings.DailyBackup = false;

        await copy.RunAsync();

        Directory.Exists(_folder).Should().BeFalse();
        _journal.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Folder_do_ktorego_nie_da_sie_pisac_zostawia_wpis_zamiast_wyjatku()
    {
        var (copy, settings) = Build();

        // Ścieżka wskazująca na plik, nie na katalog: założenie folderu o tej nazwie
        // jest niemożliwe, a jest to ten sam kształt kłopotu, co dysk, który zniknął.
        var blocker = Path.Combine(Path.GetTempPath(), $"marshal-blokada-{Guid.CreateVersion7():N}");
        await File.WriteAllTextAsync(blocker, string.Empty);
        settings.BackupFolder = Path.Combine(blocker, "kopie");

        try
        {
            var run = async () => await copy.RunAsync();

            await run.Should().NotThrowAsync("kopia nie ma prawa przewrócić aplikacji");

            _journal.Lines.Should().ContainSingle(w => w.Level == ActivityLevel.Problem);
            copy.Last().Should().BeNull();
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
