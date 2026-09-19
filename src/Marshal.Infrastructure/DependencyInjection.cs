using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.Repositories;
using Marshal.Application.Review;
using Marshal.Application.Sync;
using Marshal.Application.UseCases;
using Marshal.Domain.Diagnostics;
using Marshal.Infrastructure.Backup;
using Marshal.Infrastructure.Calendar;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Diagnostics;
using Marshal.Infrastructure.Notifications;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Review;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Sync.Google;
using Marshal.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Ścieżka bazy. Ten sam kod na Windowsie i na Androidzie: na obu platformach
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> wskazuje prywatny
    /// katalog aplikacji.
    /// </summary>
    public static string DefaultDatabasePath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Marshal");

        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "marshal.db");
    }

    /// <param name="deviceId">
    /// Wymuszony identyfikator urządzenia. Tylko do testów — normalnie bierze się
    /// go z bazy, żeby był trwały i różny na każdym urządzeniu.
    /// </param>
    public static IServiceCollection AddMarshal(
        this IServiceCollection services, string databasePath, string? deviceId = null)
    {
        // Jeden kontekst na całą aplikację **dla wszystkiego, co przechodzi przez bramę
        // kolejki**. Przy jednym użytkowniku i pracy wyłącznie lokalnej to najprostsze
        // rozwiązanie, które działa. Do ponownego rozważenia w etapie 3: scalanie
        // synchronizacji będzie chciało własnego kontekstu, żeby nie mieszać śledzenia
        // zmian z tym, co widzi ekran.
        //
        // Trzy rzeczy stoją poza bramą i dostają własne konteksty niżej: ustawienia,
        // tożsamość urządzenia i wznowienie zegara logicznego. Wszystkie trzy mają
        // odczyt **synchroniczny** — bo woła je zegar przy każdym pobraniu czasu
        // i konstruktor okna — więc nie da się ich przez bramę przepuścić inaczej niż
        // blokując wątek okna na czas cudzej pracy. Własny kontekst rozwiązuje to samo
        // bez blokowania: dwie operacje naraz szkodzą tylko wtedy, gdy trafiają w ten
        // sam kontekst.
        services.AddDbContext<MarshalDbContext>(
            options => options
                .UseSqlite($"Data Source={databasePath}")
                .AddInterceptors(new ChangeJournalInterceptor()),
            ServiceLifetime.Singleton,
            ServiceLifetime.Singleton);

        // Ustawienia przed zegarem: zegar liczy dni w strefie, którą one podają.
        //
        // **Własny kontekst.** Odczyt ustawień jest synchroniczny i idzie z wątku okna
        // — motyw przy stawianiu okna, strefa przy każdym pobraniu czasu — więc nie
        // przechodzi przez bramę kolejki. Na wspólnym kontekście znaczyło to zderzenie
        // z pracą, która akurat biegła w tle: „A second operation was started on this
        // context instance", i to przy starcie, czyli bez ekranu, na którym dałoby się
        // to pokazać. Objawiło się dopiero wtedy, gdy praca zza bramy naprawdę zaczęła
        // iść równolegle; wcześniej wszystko po kolei zajmowało ten jeden wątek.
        services.AddSingleton<ISettings>(sp => new LocalSettings(
            new MarshalDbContext(sp.GetRequiredService<DbContextOptions<MarshalDbContext>>())));
        services.AddSingleton<IClock, SystemClock>();

        // Tożsamość urządzenia też poza bramą i też synchronicznie: pyta o nią zegar
        // logiczny, a ten bywa potrzebny w konstruktorze. Własny kontekst z tego samego
        // powodu co przy ustawieniach.
        services.AddSingleton<IDeviceIdentity>(sp => deviceId is null
            ? new DeviceIdentity(
                new MarshalDbContext(sp.GetRequiredService<DbContextOptions<MarshalDbContext>>()))
            : new FixedDeviceIdentity(deviceId));

        // Zegar wznawiany z bazy — ale dopiero przy pierwszym użyciu. Fabryka jest
        // wołana przy rozwiązywaniu usługi, czyli przy składaniu okna, a baza jest
        // gotowa dopiero po PrepareAsync. Odczyt tutaj znaczył wyjątek z konstruktora
        // okna: białe tło i natychmiastowe zamknięcie, na obu platformach.
        services.AddSingleton<IHlcSource>(sp =>
        {
            // Własny kontekst, jak wyżej: wznowienie zegara to jeden synchroniczny
            // odczyt, wołany przy pierwszym użyciu — czyli w chwili, której nie da się
            // przewidzieć, a więc i takiej, w której w tle może biec cokolwiek innego.
            var db = new MarshalDbContext(
                sp.GetRequiredService<DbContextOptions<MarshalDbContext>>());

            var identity = sp.GetRequiredService<IDeviceIdentity>();

            return new HlcSource(
                sp.GetRequiredService<IClock>(),
                () => identity.Id,
                () => LastHlcStore.Read(db, identity.Id));
        });

        services.AddSingleton<ITaskRepository, TaskRepository>();
        services.AddSingleton<IProjectRepository, ProjectRepository>();
        services.AddSingleton<IAreaRepository, AreaRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<INoteRepository, NoteRepository>();
        services.AddSingleton<IAttachmentRepository, AttachmentRepository>();
        services.AddSingleton<ISavedFilterRepository, SavedFilterRepository>();
        services.AddSingleton<IContactRepository, ContactRepository>();
        // Brama na bazę przed jednostką pracy, bo to ona przez nią przechodzi.
        // Jedna na proces — dwie bramy to brak bramy.
        services.AddSingleton<IDbQueue, DbQueue>();
        // Znak zapisu: jeden na proces, bo podnosi go jednostka pracy, a nasłuchuje okno.
        services.AddSingleton<IWriteSignal, WriteSignal>();
        services.AddSingleton<IUnitOfWork, UnitOfWork>();

        // Dziennik bierze same opcje, nie wspólny kontekst: zapis w środku cudzej
        // operacji zatwierdziłby przy okazji jej niezapisane zmiany.
        services.AddSingleton<IActivityLog>(sp => new ActivityLog(
            sp.GetRequiredService<DbContextOptions<MarshalDbContext>>(),
            sp.GetRequiredService<IClock>()));

        services.AddSingleton<IReminderLog, ReminderLog>();
        services.AddSingleton<InAppNotifier>();
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<InAppNotifier>());

        services.AddSingleton<ICalendarStore, CalendarStore>();
        services.AddSingleton<CalendarSyncService>();

        // Odbicie zadań w kalendarzu. Rejestrowane pod interfejsem i pod własnym typem:
        // edycja zadań zna wyłącznie interfejs (inaczej dwie usługi wskazywałyby na
        // siebie i kontener nie miałby od czego zacząć), a okno woła po udostępnienie
        // i zdjęcie udostępnienia wprost.
        services.AddSingleton<TaskMirror>();

        // Pod interfejsem stoi wersja odkładająca pracę na po kliknięciu: wyrównanie
        // odbicia idzie przez sieć i trwa sekundę albo dwie, a odhaczenie zadania nie
        // może tyle trwać. Okno, które woła o udostępnienie wprost, dostaje wersję
        // nieodłożoną — tam użytkownik czeka świadomie i na wynik.
        services.AddSingleton<ITaskMirror>(sp => new DeferredMirror(
            sp.GetRequiredService<TaskMirror>(),
            sp.GetRequiredService<IActivityLog>()));

        // Kanał iCal działa bez żadnych poświadczeń, więc jest podłączony od razu.
        services.AddSingleton<HttpClient>();
        services.AddSingleton<ICalendarFeed, IcalFeed>();

        // Kalendarz Google **też** jest podłączony od razu, tyle że loguje się dopiero
        // przy pierwszym pobraniu. Wcześniej nie był zarejestrowany wcale: odświeżanie
        // przechodziło po źródłach, nie znajdowało kanału dla rodzaju Google i pomijało
        // je po cichu — włączenie API w konsoli niczego nie zmieniało, bo aplikacja
        // nigdy nie zadawała pytania.
        // Jeden obiekt, dwie role: kanał dla odświeżania i spis kalendarzy dla ekranu
        // ustawień. Dwa osobne znaczyłyby dwa logowania i dwa stany połączenia.
        services.AddSingleton(sp =>
            new GoogleCalendarGateway(sp.GetRequiredService<ISettings>(), databasePath));

        services.AddSingleton<ICalendarFeed>(
            sp => sp.GetRequiredService<GoogleCalendarGateway>());

        // Ten sam obiekt także jako pisarz: zapis potrzebuje tego samego zalogowania
        // i tej samej usługi, co odczyt. Osobna instancja logowałaby się drugi raz.
        services.AddSingleton<ICalendarWriter>(
            sp => sp.GetRequiredService<GoogleCalendarGateway>());

        services.AddSingleton<IReviewQueries, ReviewQueries>();
        services.AddSingleton<IReviewSessionRepository, ReviewSessionRepository>();
        services.AddSingleton<ReviewService>();

        services.AddSingleton<TaskEditService>();
        services.AddSingleton<FocusService>();
        services.AddSingleton<NowService>();
        services.AddSingleton<DayPlanService>();
        services.AddSingleton<DayRolloverService>();
        services.AddSingleton<ReminderService>();
        services.AddSingleton<NoteService>();
        // Treść załączników leży obok bazy, w katalogu adresowanym skrótem. Na Dysk
        // pojedzie dopiero razem z synchronizacją (spec 9.2) — do tego czasu składnica
        // lokalna jest nie prowizorką, tylko poprawnym stanem: wpis i plik są
        // rozdzielone z założenia, a droga pliku jest wymienna.
        //
        // Brak tej rejestracji był drugim błędem startu: AttachmentService jest
        // zarejestrowany, więc kontener obiecuje, że da się go utworzyć — a nie dało się.
        services.AddSingleton<IFileTransport>(
            _ => new LocalFolderFileTransport(Path.GetDirectoryName(databasePath)!));

        services.AddSingleton<AttachmentService>();
        services.AddSingleton<FilterService>();
        services.AddSingleton<BackupService>();

        // Synchronizacja z Dyskiem. Składnica powstaje dopiero przy logowaniu, więc
        // sama usługa niczego nie wymaga przy składaniu zależności — poświadczenia
        // mogą jeszcze nie istnieć i to jest stan normalny, nie awaria.
        services.AddSingleton(sp => new GoogleSyncService(
            sp.GetRequiredService<MarshalDbContext>(),
            sp.GetRequiredService<ISettings>(),
            sp.GetRequiredService<IHlcSource>(),
            sp.GetRequiredService<IDeviceIdentity>(),
            databasePath,
            sp.GetRequiredService<IDbQueue>()));
        services.AddSingleton<InboxService>();
        services.AddSingleton<StructureEditService>();
        services.AddSingleton<TagService>();

        return services;
    }

    /// <summary>
    /// Migracje, obszary początkowe, przejście dnia i przypomnienia. Wołane przy starcie.
    /// </summary>
    public static async Task PrepareAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var db = services.GetRequiredService<MarshalDbContext>();

        // Zbudowanie modelu bazy mierzone osobno od samych migracji. To dwa różne
        // koszty i dwa różne lekarstwa: model buduje się z odbicia typów przy pierwszym
        // dotknięciu kontekstu i da się go skompilować z góry, a machina migracji
        // przegląda dwadzieścia klas i pyta bazę, co już zastosowano. Z jednej liczby
        // nie wiadomo, którego z nich szukać.
        _ = db.Model;
        var model = clock.ElapsedMilliseconds;

        await db.Database.MigrateAsync(ct);
        var migrations = clock.ElapsedMilliseconds - model;

        // Identyfikator urządzenia rozstrzygany zaraz po migracji: zakłada go przy
        // pierwszym uruchomieniu, a zegar logiczny potrzebuje go do wznowienia.
        _ = services.GetRequiredService<IDeviceIdentity>().Id;
        var identity = clock.ElapsedMilliseconds - model - migrations;

        await AreaSeed.EnsureAsync(
            db,
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IHlcSource>(),
            ct);

        var seed = clock.ElapsedMilliseconds - model - migrations - identity;

        var catchUp = await CatchUpAsync(services, ct);

        // **Kalendarze idą obok, a nie na drodze.** Odświeżenie sięga po sieć, więc jego
        // czas nie zależy od nas: w dzienniku stoi przebieg, w którym wzięło 8,5 sekundy.
        // Wszystko, co czeka na gotowość — okno przy starcie i widget w odbiorniku
        // rozgłoszenia z budżetem dziesięciu sekund — czekało przez ten czas na Google.
        //
        // Puszczone bokiem, bo proces bez okna też ma prawo mieć świeże wydarzenia:
        // widget budzony budzikiem jest jedynym, który je wtedy pokaże.
        _ = RefreshCalendarsAsync(services, ct);

        // Przygotowanie jest tym, na co czeka wszystko inne: okno przy starcie i widget
        // w odbiorniku rozgłoszenia, który ma na to około dziesięciu sekund. W dzienniku
        // stoi przebieg, w którym trwało 13,8 sekundy — i z jednej liczby nie da się
        // powiedzieć, na czym. Stąd rozbicie, dopisywane tylko wtedy, gdy trwa długo.
        if (clock.ElapsedMilliseconds > 2000)
        {
            await services.GetRequiredService<IActivityLog>().RecordAsync(
                "Start: przygotowanie",
                $"razem {clock.ElapsedMilliseconds} ms — model {model} ms, "
                    + $"migracje {migrations} ms, tożsamość {identity} ms, "
                    + $"obszary {seed} ms, {catchUp}",
                ActivityLevel.Problem);
        }
    }

    /// <summary>
    /// Nadrobienie tego, co przespała zamknięta aplikacja: zaległe dni i przypomnienia.
    /// </summary>
    /// <remarks>
    /// Wołane przy starcie, przy powrocie z tła i po scaleniu synchronizacji — te trzy
    /// chwile to jedyne momenty, w których stan mógł się zmienić bez udziału okna.
    /// Powtórne wołanie nic nie psuje i na tym polega cały pomysł: nie ma zadania w tle,
    /// które musiałoby zadziałać dokładnie o północy, jest tylko różnica między datą
    /// zapisaną a dzisiejszą, zastawana przy każdym otwarciu.
    /// </remarks>
    /// <returns>Ile zajął każdy z czterech kroków — do dziennika, gdy start trwał długo.</returns>
    public static async Task<string> CatchUpAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await services.GetRequiredService<DayRolloverService>().RunAsync(ct);
        var rollover = clock.ElapsedMilliseconds;

        // Wybory z dni minionych wygasają razem z przejściem dnia — to ten sam moment
        // i ta sama zasada: nie ma zadania w tle, jest zastana różnica dat.
        await services.GetRequiredService<FocusService>().ExpireAsync(ct);
        var focus = clock.ElapsedMilliseconds - rollover;

        await services.GetRequiredService<ReminderService>().RunAsync(ct);
        var reminders = clock.ElapsedMilliseconds - rollover - focus;

        return $"przejście dnia {rollover} ms, wybory {focus} ms, "
            + $"przypomnienia {reminders} ms";
    }

    /// <summary>
    /// Odświeżenie kalendarzy — obok gotowości, nie w niej.
    /// </summary>
    /// <remarks>
    /// Jedyny krok rozruchu, który sięga po sieć, i jedyny, którego czas nie zależy od
    /// nas. Kanał, który nie odpowiedział, ma znaczyć „brak świeżych wydarzeń", a nie
    /// zatrzymać start — stąd własne przechwycenie wyjątku: nikt na to nie czeka, więc
    /// nie ma komu ich pokazać.
    /// </remarks>
    public static async Task RefreshCalendarsAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var report = await services.GetRequiredService<CalendarSyncService>()
                .RefreshAsync(ct: ct);

            if (report.Failed > 0 || clock.ElapsedMilliseconds > 3000)
            {
                await services.GetRequiredService<IActivityLog>().RecordAsync(
                    "Start: kalendarze",
                    $"{clock.ElapsedMilliseconds} ms, odświeżonych {report.Sources}, "
                        + $"nieudanych {report.Failed}",
                    report.Failed > 0 ? ActivityLevel.Problem : ActivityLevel.Ok);
            }
        }
        catch (Exception e)
        {
            await services.GetRequiredService<IActivityLog>().RecordAsync(
                "Start: kalendarze", "nie udało się", ActivityLevel.Problem, e.ToString());
        }
    }
}

/// <summary>Identyfikator podany z zewnątrz — do testów i do scenariuszy z dwoma bazami.</summary>
internal sealed class FixedDeviceIdentity(string id) : IDeviceIdentity
{
    public string Id { get; } = id;
}
