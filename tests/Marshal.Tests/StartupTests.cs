using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.Repositories;
using Marshal.Application.Review;
using Marshal.Application.UseCases;
using Marshal.Infrastructure;
using Marshal.Infrastructure.Backup;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Start aplikacji na pustym katalogu — to, czego CI nie sprawdzało wcale.
/// </summary>
/// <remarks>
/// Etap 0 był ostatnim sprawdzonym na sprzęcie. Wszystko po nim budowało się
/// i przechodziło testy jednostkowe, po czym aplikacja nie wstawała ani na Androidzie,
/// ani na Windowsie — bo żaden test nie składał zależności tak, jak robi to okno.
/// Budowanie nie jest uruchomieniem.
/// </remarks>
public sealed class StartupTests : IDisposable
{
    private readonly string _katalog = Path.Combine(
        Path.GetTempPath(), "marshal-start-" + Guid.NewGuid().ToString("N"));

    private string Sciezka => Path.Combine(_katalog, "marshal.db");

    private ServiceProvider Zloz()
    {
        Directory.CreateDirectory(_katalog);
        return new ServiceCollection().AddMarshal(Sciezka).BuildServiceProvider();
    }

    /// <summary>Wszystko, co okno wyciąga z kontenera, zanim baza w ogóle istnieje.</summary>
    private static readonly Type[] Uslugi =
    [
        typeof(IClock), typeof(ISettings), typeof(IDeviceIdentity), typeof(IHlcSource),
        typeof(ITaskRepository), typeof(IProjectRepository), typeof(IAreaRepository),
        typeof(ITagRepository), typeof(INoteRepository), typeof(IAttachmentRepository),
        typeof(ISavedFilterRepository), typeof(IUnitOfWork), typeof(IReviewQueries),
        typeof(IReviewSessionRepository), typeof(ICalendarStore), typeof(ICalendarFeed),
        typeof(CalendarSyncService), typeof(ReviewService), typeof(TaskEditService),
        typeof(FocusService), typeof(NowService), typeof(DayRolloverService),
        typeof(ReminderService), typeof(NoteService), typeof(AttachmentService),
        typeof(InboxService), typeof(TagService), typeof(FilterService),
        typeof(BackupService),
    ];

    [Fact]
    public void Zlozenie_zaleznosci_nie_siega_do_bazy()
    {
        // Sedno awarii z 17.09: okno rozwiązuje cały graf, **zanim** PrepareAsync
        // założy bazę. Fabryka zegara logicznego czytała przy tym tabelę LocalSettings,
        // której jeszcze nie ma — wyjątek leciał z konstruktora okna, więc widać było
        // białe tło i natychmiastowe zamknięcie. Fabryka nie ma prawa robić wejścia-wyjścia.
        using var uslugi = Zloz();

        foreach (var typ in Uslugi)
        {
            var wziecie = () => uslugi.GetRequiredService(typ);
            wziecie.Should().NotThrow($"„{typ.Name}” jest rozwiązywany przed migracją");
        }

        File.Exists(Sciezka).Should().BeFalse("składanie zależności nie ma prawa dotknąć bazy");
    }

    [Fact]
    public async Task Pelny_start_na_pustym_katalogu_dochodzi_do_konca()
    {
        // Dokładnie to, co robi aplikacja przy pierwszym uruchomieniu.
        using var uslugi = Zloz();

        var start = async () => await DependencyInjection.PrepareAsync(uslugi);
        await start.Should().NotThrowAsync();

        File.Exists(Sciezka).Should().BeTrue();

        var db = uslugi.GetRequiredService<MarshalDbContext>();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        db.Areas.Should().NotBeEmpty("obszary początkowe zakłada PrepareAsync");
    }

    [Fact]
    public async Task Drugie_uruchomienie_nie_zakłada_wszystkiego_od_nowa()
    {
        using (var pierwsze = Zloz())
        {
            await DependencyInjection.PrepareAsync(pierwsze);
        }

        using var drugie = Zloz();
        var start = async () => await DependencyInjection.PrepareAsync(drugie);
        await start.Should().NotThrowAsync();

        var db = drugie.GetRequiredService<MarshalDbContext>();
        db.Areas.Count().Should().Be(10, "obszary początkowe zakładane są raz");

        // Identyfikator urządzenia ma przeżyć zamknięcie aplikacji — inaczej każde
        // uruchomienie wyglądałoby dla synchronizacji jak nowe urządzenie.
        db.LocalSettings.Should().Contain(u => u.Key == "device-id");
    }

    [Fact]
    public async Task Zegar_logiczny_wznawia_sie_z_bazy_a_nie_od_zera()
    {
        using (var pierwsze = Zloz())
        {
            await DependencyInjection.PrepareAsync(pierwsze);

            var db = pierwsze.GetRequiredService<MarshalDbContext>();
            var hlc = pierwsze.GetRequiredService<IHlcSource>();

            db.Tasks.Add(Marshal.Domain.Tasks.TaskItem.Capture(
                "cokolwiek", pierwsze.GetRequiredService<IClock>().Now, hlc.Next()));

            await db.SaveChangesAsync();
        }

        using var drugie = Zloz();
        await DependencyInjection.PrepareAsync(drugie);

        drugie.GetRequiredService<IHlcSource>().Last.WallMs.Should().BeGreaterThan(0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_katalog))
            {
                Directory.Delete(_katalog, recursive: true);
            }
        }
        catch (IOException)
        {
            // Sprzątanie katalogu tymczasowego nie jest tym, co ten test sprawdza.
        }
    }
}
