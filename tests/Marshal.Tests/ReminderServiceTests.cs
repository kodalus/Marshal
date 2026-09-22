using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Notifications;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

[Collection("Powiadomienia systemowe")]
public sealed class ReminderServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    /// <summary>Strefa testu — ta sama, w której liczone są chwile w asercjach.</summary>
    private sealed class NewZone : ISettings
    {
        public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

        public string? ZoneProblem => null;

        public ThemeChoice Theme => ThemeChoice.System;

        public string? GoogleClientId => null;

        public string? GoogleClientSecret => null;

        public bool GoogleCalendarEnabled => false;

        public Guid? MainCalendarId => null;

        public void SetMainCalendar(Guid? calendarId) => throw new NotSupportedException();

        public IReadOnlyList<string> CalendarAccounts => [];

        public void AddCalendarAccount(string email) => throw new NotSupportedException();

        public void RemoveCalendarAccount(string email) => throw new NotSupportedException();

        public void SetGoogleCalendarEnabled(bool enabled) => throw new NotSupportedException();

        public void SetZone(string id) => throw new NotSupportedException();

        public void SetTheme(ThemeChoice theme) => throw new NotSupportedException();

        public void SetGoogle(string? clientId, string? clientSecret) =>
            throw new NotSupportedException();

        public bool DailyBackup => false;

        public void SetDailyBackup(bool on) => throw new NotSupportedException();

        public string? BackupFolder => null;

        public void SetBackupFolder(string? path) => throw new NotSupportedException();
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly InAppNotifier _powiadamiacz = new();
    private readonly ReminderService _service;
    private readonly HlcSource _hlc;
    private readonly Guid _area = Guid.CreateVersion7();

    public ReminderServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_clock, "biurko");

        _service = new ReminderService(
            new TaskRepository(_db),
            new ReminderLog(_db),
            _powiadamiacz,
            new UnitOfWork(_db),
            new NewZone(),
            _clock);
    }

    private static DateTimeOffset Moment(string iso) =>
        DateTimeOffset.Parse(iso + "+02:00");

    private TaskItem Add(string title, string? reminder)
    {
        var task = TaskItem.Capture(title, _clock.Now, _hlc.Next());
        task.MakeNext(_area, _hlc.Next());

        if (reminder is not null)
        {
            task.SetReminder(Moment(reminder), _hlc.Next());
        }

        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    [Fact]
    public async Task Przypomnienie_z_przyszlosci_milczy()
    {
        Add("Zadzwonić", "2026-09-16T18:00:00");

        (await _service.RunAsync()).Should().Be(0);
        _powiadamiacz.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task Przypomnienie_wymagalne_odzywa_sie()
    {
        var id = Add("Zadzwonić do przychodni", "2026-09-16T08:00:00").Id;

        (await _service.RunAsync()).Should().Be(1);

        var shown = _powiadamiacz.Drain();
        shown.Should().ContainSingle();
        shown[0].TaskId.Should().Be(id);
        shown[0].Title.Should().Be("Zadzwonić do przychodni");
    }

    [Fact]
    public async Task Przypomnienie_z_wczoraj_tez_sie_odzywa()
    {
        // Aplikacja nie chodzi w tle, więc chwila przypomnienia prawie nigdy nie zastaje
        // jej otwartej. Odzywanie się wyłącznie co do minuty znaczyłoby, że przypomnienia
        // nie działają w ogóle.
        Add("Zapłacić ratę", "2026-09-14T20:00:00");

        (await _service.RunAsync()).Should().Be(1);
    }

    /// <summary>
    /// Zadanie z godziną odzywa się tyle razy, ile ma wyprzedzeń.
    /// </summary>
    /// <remarks>
    /// Tak się o tym myśli: „przypomnij mi pół godziny przed wizytą", a nie „przypomnij
    /// o 14:30". Przy przesunięciu zadania wyprzedzenia jadą razem z nim i nie trzeba
    /// ich poprawiać po jednym.
    /// </remarks>
    [Fact]
    public async Task Zadanie_z_godzina_odzywa_sie_z_kazdego_wyprzedzenia()
    {
        var task = TaskItem.Capture("Wizyta", _clock.Now, _hlc.Next());
        task.Schedule(_area, new DateOnly(2026, 9, 16), _hlc.Next());
        task.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        task.SetReminderLeads([0, 15, 60], _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        // Kwadrans po dziewiątej: minęło wyprzedzenie godzinne, reszta jeszcze nie.
        _clock.Now = Moment("2026-09-16T09:15:00");
        (await _service.RunAsync()).Should().Be(1);

        _clock.Now = Moment("2026-09-16T09:50:00");
        (await _service.RunAsync()).Should().Be(1, "kwadrans przed, godzinne już było");

        _clock.Now = Moment("2026-09-16T10:00:00");
        (await _service.RunAsync()).Should().Be(1, "o czasie");

        _clock.Now = Moment("2026-09-16T10:30:00");
        (await _service.RunAsync()).Should().Be(0, "wszystkie trzy już się odezwały");
    }

    /// <summary>
    /// Przesunięcie zadania przesuwa jego przypomnienia, bez dotykania wyprzedzeń.
    /// </summary>
    [Fact]
    public async Task Przesuniete_zadanie_zabiera_wyprzedzenia_ze_soba()
    {
        var task = TaskItem.Capture("Wizyta", _clock.Now, _hlc.Next());
        task.Schedule(_area, new DateOnly(2026, 9, 16), _hlc.Next());
        task.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        task.SetReminderLeads([30], _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();

        _clock.Now = Moment("2026-09-16T09:31:00");
        (await _service.RunAsync()).Should().Be(1);

        // Ta sama godzina, następny dzień: to inna chwila, więc odzywa się na nowo.
        task.MoveDoDate(new DateOnly(2026, 9, 17), _hlc.Next());
        _db.SaveChanges();

        _clock.Now = Moment("2026-09-17T09:31:00");
        (await _service.RunAsync()).Should().Be(1);
    }

    /// <summary>
    /// Wyprzedzenia sprzed więcej niż doby milczą; własna chwila przypomnienia nie.
    /// </summary>
    /// <remarks>
    /// Wyprzedzeń bywa kilka na zadanie, więc tydzień zamkniętej aplikacji dałby ich
    /// kilkadziesiąt naraz — lawinę do zamknięcia, nie przypomnienia. Przypomnienie
    /// z własną chwilą jest jedno i ustawione ręcznie, więc odzywa się bez względu
    /// na to, jak długo aplikacja była zamknięta.
    /// </remarks>
    [Fact]
    public async Task Stare_wyprzedzenia_milcza_a_wlasna_chwila_nie()
    {
        var zWyprzedzeniem = TaskItem.Capture("Wizyta", _clock.Now, _hlc.Next());
        zWyprzedzeniem.Schedule(_area, new DateOnly(2026, 9, 10), _hlc.Next());
        zWyprzedzeniem.SetDoTime(new TimeOnly(10, 0), _hlc.Next());
        zWyprzedzeniem.SetReminderLeads([0, 15, 60], _hlc.Next());
        _db.Tasks.Add(zWyprzedzeniem);

        Add("Zapłacić ratę", "2026-09-14T20:00:00");

        (await _service.RunAsync()).Should().Be(1, "tylko to z własną chwilą");
    }

    [Fact]
    public async Task To_samo_przypomnienie_nie_odzywa_sie_dwa_razy()
    {
        Add("Zadzwonić", "2026-09-16T08:00:00");

        await _service.RunAsync();
        _powiadamiacz.Drain();

        (await _service.RunAsync()).Should().Be(0);
        _powiadamiacz.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task Przesuniete_przypomnienie_odzywa_sie_ponownie()
    {
        // „Przypomnij mi jednak o godzinę później" musi zadziałać. Gdyby zapis
        // pokazania znaczył tylko „o tym zadaniu już było", nowa chwila by przepadła.
        var task = Add("Zadzwonić", "2026-09-16T08:00:00");
        await _service.RunAsync();
        _powiadamiacz.Drain();

        task.SetReminder(Moment("2026-09-16T08:30:00"), _hlc.Next());
        _db.SaveChanges();

        (await _service.RunAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Wykonane_zadanie_nie_przypomina_o_sobie()
    {
        var task = Add("Zadzwonić", "2026-09-16T08:00:00");
        task.Complete(_clock.Now, _hlc.Next());
        _db.SaveChanges();

        (await _service.RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Zadanie_bez_przypomnienia_milczy()
    {
        Add("Bez przypomnienia", null);

        (await _service.RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Zalegle_przypomnienia_ida_w_kolejnosci_chwil()
    {
        Add("Trzecie", "2026-09-16T08:00:00");
        Add("Pierwsze", "2026-09-14T08:00:00");
        Add("Drugie", "2026-09-15T08:00:00");

        await _service.RunAsync();

        _powiadamiacz.Drain().Select(p => p.Title)
            .Should().Equal("Pierwsze", "Drugie", "Trzecie");
    }

    [Fact]
    public async Task Zapis_pokazania_nie_trafia_do_dziennika_zmian()
    {
        // Gdyby trafiał, drugie urządzenie dowiedziałoby się, że tu już pokazano —
        // i zamilkło, choć telefon leżał w torbie.
        Add("Zadzwonić", "2026-09-16T08:00:00");
        var before = _db.Changes.Count();

        await _service.RunAsync();

        _db.Changes.Count().Should().Be(before);
        _db.ReminderShown.Should().ContainSingle();
    }

    [Fact]
    public async Task Przypomnienie_przenosi_sie_na_kolejne_wystapienie_z_zachowaniem_pory()
    {
        // „W przeddzień o dwudziestej" ma zostać przeddniem o dwudziestej, a nie
        // przenieść się co do daty i odezwać się natychmiast.
        var task = TaskItem.Capture("Wynieść śmieci", _clock.Now, _hlc.Next());
        task.Schedule(_area, new DateOnly(2026, 9, 14), _hlc.Next());
        task.SetReminder(Moment("2026-09-13T20:00:00"), _hlc.Next());
        task.SetRecurrence(
            new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday), _hlc.Next());

        var next = RecurrenceRunner.Complete(task, _clock.Now, _hlc.Next)!;

        next.DoDate.Should().Be(new DateOnly(2026, 9, 21));
        next.ReminderAt.Should().Be(Moment("2026-09-20T20:00:00"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
