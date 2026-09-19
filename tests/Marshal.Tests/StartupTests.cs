using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure;
using Marshal.Infrastructure.Data;
using Marshal.UI;
using Marshal.UI.ViewModels;
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
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "marshal-start-" + Guid.NewGuid().ToString("N"));

    private string PathOf => Path.Combine(_folder, "marshal.db");

    /// <summary>Dokładnie ten sam graf, który składa okno — usługi plus modele widoków.</summary>
    private ServiceProvider Build()
    {
        Directory.CreateDirectory(_folder);

        return new ServiceCollection()
            .AddMarshal(PathOf)
            .AddMarshalViewModels()
            .BuildServiceProvider();
    }

    [Fact]
    public void Zlozenie_zaleznosci_nie_siega_do_bazy()
    {
        // Dwie rzeczy naraz, obie odpowiedzialne za awarię z 17.09.
        //
        // Po pierwsze: **wszystko, co zarejestrowane, musi dać się utworzyć**. Rejestracja
        // jest obietnicą kontenera; usługa zarejestrowana, której brakuje zależności,
        // to mina, która wybucha przy pierwszym sięgnięciu. Tak było z AttachmentService,
        // bo IFileTransport nie został zarejestrowany wcale.
        //
        // Po drugie: **żadna fabryka nie ma prawa robić wejścia-wyjścia**. Okno rozwiązuje
        // cały graf, zanim PrepareAsync założy bazę; fabryka zegara logicznego czytała
        // przy tym tabelę, której jeszcze nie ma. Objawem było białe tło i natychmiastowe
        // zamknięcie, na obu platformach.
        var kolekcja = new ServiceCollection();
        kolekcja.AddMarshal(PathOf).AddMarshalViewModels();
        Directory.CreateDirectory(_folder);

        using var services = kolekcja.BuildServiceProvider();

        // Po naszych typach, nie po wszystkich: rejestracje wewnętrzne EF Core bywają
        // zakresowe i nie są tym, co ten test pilnuje.
        var ours = kolekcja
            .Select(r => r.ServiceType)
            .Where(t => t.Assembly.GetName().Name?.StartsWith("Marshal.", StringComparison.Ordinal) == true)
            .Where(t => !t.IsGenericTypeDefinition)
            .Distinct()
            .ToList();

        ours.Should().HaveCountGreaterThan(20, "test miał objąć cały graf, nie jego resztkę");

        // Wszystkie naraz, nie do pierwszego napotkanego: jedna awaria na przebieg
        // znaczyłaby jeden błąd na przebieg budowania.
        var pekniete = new List<string>();

        foreach (var typ in ours)
        {
            try
            {
                services.GetRequiredService(typ);
            }
            catch (Exception e)
            {
                pekniete.Add($"{typ.Name}: {e.Message}");
            }
        }

        pekniete.Should().BeEmpty();

        File.Exists(PathOf).Should().BeFalse("składanie zależności nie ma prawa dotknąć bazy");
    }

    [Fact]
    public async Task Pelny_start_na_pustym_katalogu_dochodzi_do_konca()
    {
        // Dokładnie to, co robi aplikacja przy pierwszym uruchomieniu.
        using var services = Build();

        var start = async () => await DependencyInjection.PrepareAsync(services);
        await start.Should().NotThrowAsync();

        File.Exists(PathOf).Should().BeTrue();

        var db = services.GetRequiredService<MarshalDbContext>();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        db.Areas.Should().NotBeEmpty("obszary początkowe zakłada PrepareAsync");
    }

    [Fact]
    public async Task Okno_dostaje_model_widoku_i_wczytuje_pierwszy_ekran()
    {
        // Ostatni krok startu: to, co aplikacja robi po PrepareAsync. Gdyby któryś
        // model widoku miał niezarejestrowaną zależność, objaw byłby ten sam co
        // 17.09 — okno znika, nie mówiąc dlaczego.
        using var services = Build();
        await DependencyInjection.PrepareAsync(services);

        var model = services.GetRequiredService<MainViewModel>();

        var firstScreen = async () => await model.InitializeAsync();
        await firstScreen.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Drugie_uruchomienie_nie_zakłada_wszystkiego_od_nowa()
    {
        using (var first = Build())
        {
            await DependencyInjection.PrepareAsync(first);
        }

        using var drugie = Build();
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
        using (var first = Build())
        {
            await DependencyInjection.PrepareAsync(first);

            var db = first.GetRequiredService<MarshalDbContext>();
            var hlc = first.GetRequiredService<IHlcSource>();

            db.Tasks.Add(Marshal.Domain.Tasks.TaskItem.Capture(
                "cokolwiek", first.GetRequiredService<IClock>().Now, hlc.Next()));

            await db.SaveChangesAsync();
        }

        using var drugie = Build();
        await DependencyInjection.PrepareAsync(drugie);

        drugie.GetRequiredService<IHlcSource>().Last.WallMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Odczyt_ustawien_znosi_prace_biegnaca_rownolegle_w_tle()
    {
        // Objaw: „A second operation was started on this context instance" przy samym
        // starcie, czyli bez ekranu, na którym dałoby się cokolwiek pokazać.
        //
        // Ustawienia czyta się **synchronicznie i z wątku okna** — motyw przy stawianiu
        // okna, strefa przy każdym pobraniu czasu — więc nie przechodzą przez bramę
        // kolejki. Dopóki praca zza bramy wykonywała się po kolei na tym samym wątku,
        // nie miało to jak się zderzyć. Odkąd brama zeszła na wątek z puli, zaczęło.
        using var services = Build();
        await DependencyInjection.PrepareAsync(services);

        var settings = services.GetRequiredService<ISettings>();
        var tasks = services.GetRequiredService<Marshal.Application.Repositories.ITaskRepository>();

        using var end = new CancellationTokenSource();

        // W tle to, co przechodzi przez bramę: prawdziwe odczyty na wspólnym kontekście.
        var wTle = Task.Run(
            async () =>
            {
                while (!end.IsCancellationRequested)
                {
                    await tasks.AllAsync(end.Token);
                }
            },
            end.Token);

        // A tutaj to, co bramę omija — tak jak robi to okno przy starcie. Z zapisem,
        // bo odczytane ustawienie zostaje w pamięci: bez zapisu druga i każda następna
        // pętla nie tknęłaby już bazy i test sprawdzałby samo pole w obiekcie.
        var proba = () =>
        {
            for (var i = 0; i < 30; i++)
            {
                settings.SetTheme(i % 2 == 0 ? ThemeChoice.Dark : ThemeChoice.Light);

                _ = settings.Theme;
                _ = settings.Zone;
            }
        };

        proba.Should().NotThrow(
            "ustawienia mają własny kontekst, więc cudza praca nie ma w co uderzyć");

        await end.CancelAsync();

        try
        {
            await wTle;
        }
        catch (OperationCanceledException)
        {
            // Zatrzymanie pracy w tle jest końcem testu, nie jego wynikiem.
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // Sprzątanie katalogu tymczasowego nie jest tym, co ten test sprawdza.
        }
    }
}
