using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Review;
using Marshal.Domain.Areas;
using Marshal.Domain.Review;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Review;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Kreator przeglądu (spec 8.3). Najważniejsza własność: przerwanie w dowolnym
/// momencie nie kosztuje nic.
/// </summary>
public sealed class ReviewServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 20, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly HlcSource _hlc;
    private readonly ReviewService _review;
    private readonly Area _area;

    public ReviewServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_clock, "biurko");

        _review = new ReviewService(
            new ReviewQueries(_db),
            new TaskRepository(_db),
            new ReviewSessionRepository(_db),
            new UnitOfWork(_db),
            _clock,
            _hlc);

        _area = new Area(Guid.CreateVersion7(), _clock.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(_area);
        _db.SaveChanges();
    }

    private TaskItem NewCapture(string title)
    {
        var task = TaskItem.Capture(title, _clock.Now, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    [Fact]
    public async Task Pierwsze_otwarcie_zaklada_przeglad()
    {
        var sesja = await _review.StartOrResumeAsync();

        sesja.CurrentStep.Should().Be(0);
        sesja.IsCompleted.Should().BeFalse();
        sesja.ProcessedCount.Should().Be(0);
    }

    [Fact]
    public async Task Kolejne_otwarcie_wraca_do_tego_samego_przegladu()
    {
        // Wznowienie jest domyślne i nie pyta. Ekran „masz niedokończony przegląd, wrócić?"
        // dawałby okazję do zaczęcia od nowa, czyli do tego, przed czym ten mechanizm chroni.
        var first = await _review.StartOrResumeAsync();
        await _review.GoToAsync(first, 3);

        var drugie = await _review.StartOrResumeAsync();

        drugie.Id.Should().Be(first.Id);
        drugie.CurrentStep.Should().Be(3);
    }

    [Fact]
    public async Task Ukonczony_przeglad_nie_jest_wznawiany()
    {
        var first = await _review.StartOrResumeAsync();
        await _review.CompleteAsync(first);

        var drugie = await _review.StartOrResumeAsync();

        drugie.Id.Should().NotBe(first.Id);
        drugie.CurrentStep.Should().Be(0);
    }

    [Fact]
    public async Task Nieukonczony_przeglad_nie_wygasa()
    {
        // Bez presji terminu i bez czerwonej plakietki: przegląd rozłożony na pięć
        // wieczorów jest przeglądem zrobionym (8.3).
        var sesja = await _review.StartOrResumeAsync();
        _clock.Now = _clock.Now.AddDays(21);

        (await _review.StartOrResumeAsync()).Id.Should().Be(sesja.Id);
    }

    [Fact]
    public async Task Rozpatrzona_pozycja_znika_z_kroku()
    {
        NewCapture("pierwsze");
        var drugie = NewCapture("drugie");
        var sesja = await _review.StartOrResumeAsync();

        (await _review.ItemsAsync(ReviewStep.Inbox, sesja)).Should().HaveCount(2);

        await _review.MarkProcessedAsync(sesja, drugie.Id);

        (await _review.ItemsAsync(ReviewStep.Inbox, sesja)).Select(i => i.Title)
            .Should().Equal("pierwsze");
    }

    [Fact]
    public async Task Stan_przezywa_zamkniecie_aplikacji()
    {
        // Sedno mechanizmu: przerwanie przez dziecko po czterech minutach to przegląd
        // w trakcie, a nie przegląd do zrobienia od nowa.
        var first = NewCapture("pierwsze");
        NewCapture("drugie");

        var sesja = await _review.StartOrResumeAsync();
        await _review.MarkProcessedAsync(sesja, first.Id);
        await _review.GoToAsync(sesja, 4);

        _db.ChangeTracker.Clear();

        var wznowiona = await _review.StartOrResumeAsync();
        wznowiona.CurrentStep.Should().Be(4);
        wznowiona.IsProcessed(first.Id).Should().BeTrue();
        (await _review.ItemsAsync(ReviewStep.Inbox, wznowiona)).Select(i => i.Title)
            .Should().Equal("drugie");
    }

    [Fact]
    public async Task Ta_sama_pozycja_oznaczona_dwa_razy_liczy_sie_raz()
    {
        var capture = NewCapture("pierwsze");
        var sesja = await _review.StartOrResumeAsync();

        await _review.MarkProcessedAsync(sesja, capture.Id);
        await _review.MarkProcessedAsync(sesja, capture.Id);

        sesja.ProcessedCount.Should().Be(1);
    }

    [Fact]
    public async Task Krok_zerowy_i_osmy_nie_maja_pozycji_do_odhaczenia()
    {
        // Pierwszy jest tekstem do przeczytania, drugi tabelą do obejrzenia.
        // Nie każdy krok przeglądu kończy się czynnością — to jest celowe.
        NewCapture("cokolwiek");
        var sesja = await _review.StartOrResumeAsync();

        (await _review.ItemsAsync(ReviewStep.Pinned, sesja)).Should().BeEmpty();
        (await _review.ItemsAsync(ReviewStep.Balance, sesja)).Should().BeEmpty();
    }

    [Fact]
    public async Task Liczniki_licza_to_co_kroki_pokazuja()
    {
        NewCapture("w skrzynce");

        var przeterminowane = TaskItem.Capture("po terminie", _clock.Now, _hlc.Next());
        przeterminowane.MakeNext(_area.Id, _hlc.Next());
        przeterminowane.SetDeadline(new DateOnly(2026, 9, 1), _hlc.Next());
        _db.Tasks.Add(przeterminowane);

        var pending = TaskItem.Capture("na kimś", _clock.Now, _hlc.Next());
        pending.Delegate(_area.Id, "urząd", new DateOnly(2026, 8, 1), null, _hlc.Next());
        _db.Tasks.Add(pending);
        _db.SaveChanges();

        var liczniki = await _review.CountsAsync();

        liczniki.Inbox.Should().Be(1);
        liczniki.Overdue.Should().Be(1);
        liczniki.Nudges.Should().Be(1);
        liczniki.Total.Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task Zbior_rozpatrzonych_przezywa_zapis_do_bazy()
    {
        // Zbiór idzie do bazy jako tekst. Gdyby odczyt go nie odtwarzał, wznowiony
        // przegląd pokazywałby wszystko od nowa — i to bez żadnego błędu.
        var capture = NewCapture("pierwsze");
        var sesja = await _review.StartOrResumeAsync();
        await _review.MarkProcessedAsync(sesja, capture.Id);

        _db.ChangeTracker.Clear();

        var read = _db.ReviewSessions.Single();
        read.IsProcessed(capture.Id).Should().BeTrue();
        read.ProcessedCount.Should().Be(1);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
