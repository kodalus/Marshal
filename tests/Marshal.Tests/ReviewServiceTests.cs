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

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _zegar = new();
    private readonly HlcSource _hlc;
    private readonly ReviewService _przeglad;
    private readonly Area _obszar;

    public ReviewServiceTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_zegar, "biurko");

        _przeglad = new ReviewService(
            new ReviewQueries(_db),
            new TaskRepository(_db),
            new ReviewSessionRepository(_db),
            new UnitOfWork(_db),
            _zegar,
            _hlc);

        _obszar = new Area(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), "Dom", 0);
        _db.Areas.Add(_obszar);
        _db.SaveChanges();
    }

    private TaskItem Wrzut(string title)
    {
        var task = TaskItem.Capture(title, _zegar.Now, _hlc.Next());
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    [Fact]
    public async Task Pierwsze_otwarcie_zaklada_przeglad()
    {
        var sesja = await _przeglad.StartOrResumeAsync();

        sesja.CurrentStep.Should().Be(0);
        sesja.IsCompleted.Should().BeFalse();
        sesja.ProcessedCount.Should().Be(0);
    }

    [Fact]
    public async Task Kolejne_otwarcie_wraca_do_tego_samego_przegladu()
    {
        // Wznowienie jest domyślne i nie pyta. Ekran „masz niedokończony przegląd, wrócić?"
        // dawałby okazję do zaczęcia od nowa, czyli do tego, przed czym ten mechanizm chroni.
        var pierwsze = await _przeglad.StartOrResumeAsync();
        await _przeglad.GoToAsync(pierwsze, 3);

        var drugie = await _przeglad.StartOrResumeAsync();

        drugie.Id.Should().Be(pierwsze.Id);
        drugie.CurrentStep.Should().Be(3);
    }

    [Fact]
    public async Task Ukonczony_przeglad_nie_jest_wznawiany()
    {
        var pierwsze = await _przeglad.StartOrResumeAsync();
        await _przeglad.CompleteAsync(pierwsze);

        var drugie = await _przeglad.StartOrResumeAsync();

        drugie.Id.Should().NotBe(pierwsze.Id);
        drugie.CurrentStep.Should().Be(0);
    }

    [Fact]
    public async Task Nieukonczony_przeglad_nie_wygasa()
    {
        // Bez presji terminu i bez czerwonej plakietki: przegląd rozłożony na pięć
        // wieczorów jest przeglądem zrobionym (8.3).
        var sesja = await _przeglad.StartOrResumeAsync();
        _zegar.Now = _zegar.Now.AddDays(21);

        (await _przeglad.StartOrResumeAsync()).Id.Should().Be(sesja.Id);
    }

    [Fact]
    public async Task Rozpatrzona_pozycja_znika_z_kroku()
    {
        Wrzut("pierwsze");
        var drugie = Wrzut("drugie");
        var sesja = await _przeglad.StartOrResumeAsync();

        (await _przeglad.ItemsAsync(ReviewStep.Inbox, sesja)).Should().HaveCount(2);

        await _przeglad.MarkProcessedAsync(sesja, drugie.Id);

        (await _przeglad.ItemsAsync(ReviewStep.Inbox, sesja)).Select(i => i.Title)
            .Should().Equal("pierwsze");
    }

    [Fact]
    public async Task Stan_przezywa_zamkniecie_aplikacji()
    {
        // Sedno mechanizmu: przerwanie przez dziecko po czterech minutach to przegląd
        // w trakcie, a nie przegląd do zrobienia od nowa.
        var pierwsze = Wrzut("pierwsze");
        Wrzut("drugie");

        var sesja = await _przeglad.StartOrResumeAsync();
        await _przeglad.MarkProcessedAsync(sesja, pierwsze.Id);
        await _przeglad.GoToAsync(sesja, 4);

        _db.ChangeTracker.Clear();

        var wznowiona = await _przeglad.StartOrResumeAsync();
        wznowiona.CurrentStep.Should().Be(4);
        wznowiona.IsProcessed(pierwsze.Id).Should().BeTrue();
        (await _przeglad.ItemsAsync(ReviewStep.Inbox, wznowiona)).Select(i => i.Title)
            .Should().Equal("drugie");
    }

    [Fact]
    public async Task Ta_sama_pozycja_oznaczona_dwa_razy_liczy_sie_raz()
    {
        var wrzut = Wrzut("pierwsze");
        var sesja = await _przeglad.StartOrResumeAsync();

        await _przeglad.MarkProcessedAsync(sesja, wrzut.Id);
        await _przeglad.MarkProcessedAsync(sesja, wrzut.Id);

        sesja.ProcessedCount.Should().Be(1);
    }

    [Fact]
    public async Task Krok_zerowy_i_osmy_nie_maja_pozycji_do_odhaczenia()
    {
        // Pierwszy jest tekstem do przeczytania, drugi tabelą do obejrzenia.
        // Nie każdy krok przeglądu kończy się czynnością — to jest celowe.
        Wrzut("cokolwiek");
        var sesja = await _przeglad.StartOrResumeAsync();

        (await _przeglad.ItemsAsync(ReviewStep.Pinned, sesja)).Should().BeEmpty();
        (await _przeglad.ItemsAsync(ReviewStep.Balance, sesja)).Should().BeEmpty();
    }

    [Fact]
    public async Task Liczniki_licza_to_co_kroki_pokazuja()
    {
        Wrzut("w skrzynce");

        var przeterminowane = TaskItem.Capture("po terminie", _zegar.Now, _hlc.Next());
        przeterminowane.MakeNext(_obszar.Id, _hlc.Next());
        przeterminowane.SetDeadline(new DateOnly(2026, 9, 1), _hlc.Next());
        _db.Tasks.Add(przeterminowane);

        var pending = TaskItem.Capture("na kimś", _zegar.Now, _hlc.Next());
        pending.Delegate(_obszar.Id, "urząd", new DateOnly(2026, 8, 1), null, _hlc.Next());
        _db.Tasks.Add(pending);
        _db.SaveChanges();

        var liczniki = await _przeglad.CountsAsync();

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
        var wrzut = Wrzut("pierwsze");
        var sesja = await _przeglad.StartOrResumeAsync();
        await _przeglad.MarkProcessedAsync(sesja, wrzut.Id);

        _db.ChangeTracker.Clear();

        var read = _db.ReviewSessions.Single();
        read.IsProcessed(wrzut.Id).Should().BeTrue();
        read.ProcessedCount.Should().Be(1);
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}
