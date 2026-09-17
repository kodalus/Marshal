using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Calendar;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Calendar;

/// <summary>
/// Wynik odświeżania. **Z powodami**, nie samą liczbą porażek.
/// </summary>
/// <remarks>
/// Sama liczba nie mówi nic: „nieudanych 6" wygląda tak samo, gdy nie ma zgody na
/// kalendarz, gdy adres kanału jest zły i gdy padła sieć. Kalendarz zewnętrzny jest
/// jedynym miejscem w aplikacji, gdzie **wszystkie** przyczyny są po cudzej stronie,
/// więc powód jest tu jedyną rzeczą, z której da się cokolwiek zrobić.
/// </remarks>
public sealed record CalendarRefreshReport(
    int Sources, int Events, int Failed, IReadOnlyList<string> Problems)
{
    public CalendarRefreshReport(int sources, int events, int failed)
        : this(sources, events, failed, [])
    {
    }
}

/// <summary>
/// Kalendarz: odświeżanie źródeł i składanie siatki (spec 10.1, 11).
/// </summary>
public sealed class CalendarSyncService(
    ICalendarStore store,
    ITaskRepository tasks,
    IEnumerable<ICalendarFeed> feeds,
    IClock clock,
    IHlcSource hlc,
    ISettings settings,
    IEnumerable<ICalendarWriter> writers,
    IProjectRepository projects,
    IAreaRepository areas)
{
    /// <summary>Czy do tego kalendarza da się pisać. Na ekran — żeby nie kusić przyciskiem bez skutku.</summary>
    public bool CanWrite(CalendarKind kind) => writers.Any(w => w.Kind == kind);

    /// <summary>
    /// Zapis wydarzenia u źródła i w naszej kopii (spec 10.2).
    /// </summary>
    /// <remarks>
    /// Kolejność jest treścią, nie szczegółem: najpierw Google, potem baza. Odwrotna
    /// zostawiałaby przy nieudanym zapisie wydarzenie widoczne w Marshalu, a nieistniejące
    /// nigdzie indziej — czyli dokładnie to, przed czym zapis dwustronny miał chronić.
    /// </remarks>
    public async Task<string> SaveEventAsync(
        Guid sourceId, string? externalId, CalendarDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var (zrodlo, pisarz) = await DoZapisuAsync(sourceId, ct);

        var identyfikator = string.IsNullOrWhiteSpace(externalId)
            ? await pisarz.CreateAsync(zrodlo, draft, ct)
            : externalId;

        if (!string.IsNullOrWhiteSpace(externalId))
        {
            await pisarz.UpdateAsync(zrodlo, externalId, draft, ct);
        }

        await store.UpsertAsync(
            zrodlo.Id,
            [new FeedEvent(
                identyfikator, draft.Title, draft.Start, draft.End,
                IsAllDay: false, draft.Location, Cancelled: false)],
            ct);

        await store.SaveChangesAsync(ct);

        return identyfikator;
    }

    /// <summary>Skasowanie wydarzenia u źródła i u nas. Tylko to wskazane wprost.</summary>
    public async Task DeleteEventAsync(
        Guid sourceId, string externalId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        var (zrodlo, pisarz) = await DoZapisuAsync(sourceId, ct);

        await pisarz.DeleteAsync(zrodlo, externalId, ct);

        // U nas nagrobek, nie usunięcie — tak samo jak wszędzie indziej w tym modelu.
        var nasze = await store.EventsAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        if (nasze.FirstOrDefault(e => e.SourceId == sourceId && e.ExternalId == externalId)
            is { } wydarzenie)
        {
            await store.UpsertAsync(
                sourceId,
                [new FeedEvent(
                    externalId, wydarzenie.Title, wydarzenie.StartsAt, wydarzenie.EndsAt,
                    wydarzenie.IsAllDay, wydarzenie.Location, Cancelled: true)],
                ct);

            await store.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Odhaczenie wydarzenia z podłączonego kalendarza — ptaszkiem przy jego nazwie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wydarzenie nie ma u nas stanu „zrobione” i nie powinno mieć: własna kolumna
    /// znaczyłaby ptaszek widoczny wyłącznie w Marshalu, a to jest jedyne miejsce,
    /// w którym i tak patrzy się najrzadziej. Znak w nazwie widać w Google, na telefonie
    /// i w powiadomieniu, a po odświeżeniu wraca do nas sam.
    /// </para>
    /// <para>
    /// Kolejność jak przy każdym innym zapisie na zewnątrz: najpierw źródło, potem nasza
    /// kopia. Odwrotna zostawiałaby przy nieudanym zapisie ptaszek widoczny u nas
    /// i nieistniejący nigdzie indziej — czyli dokładnie to kłamstwo, przed którym
    /// zapis dwustronny ma chronić.
    /// </para>
    /// </remarks>
    public async Task SetEventDoneAsync(
        Guid sourceId, string externalId, bool done, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        var (zrodlo, pisarz) = await DoZapisuAsync(sourceId, ct);

        var nasze = await store.EventsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        if (nasze.FirstOrDefault(e => e.SourceId == sourceId && e.ExternalId == externalId)
            is not { } wydarzenie)
        {
            throw new InvalidOperationException(
                "Tego wydarzenia nie ma już w pobranej kopii kalendarza. Odśwież i spróbuj raz jeszcze.");
        }

        var nazwa = EventMark.Set(wydarzenie.Title, done);

        if (nazwa == wydarzenie.Title)
        {
            return;
        }

        await pisarz.RenameAsync(zrodlo, externalId, nazwa, ct);

        await store.UpsertAsync(
            sourceId,
            [new FeedEvent(
                externalId, nazwa, wydarzenie.StartsAt, wydarzenie.EndsAt,
                wydarzenie.IsAllDay, wydarzenie.Location, Cancelled: false)],
            ct);

        await store.SaveChangesAsync(ct);
    }

    private async Task<(CalendarSource Source, ICalendarWriter Writer)> DoZapisuAsync(
        Guid sourceId, CancellationToken ct)
    {
        var zrodlo = (await store.SourcesAsync(ct)).FirstOrDefault(z => z.Id == sourceId)
            ?? throw new InvalidOperationException(
                "Tego kalendarza już nie ma na liście podłączonych.");

        var pisarz = writers.FirstOrDefault(w => w.Kind == zrodlo.Kind)
            ?? throw new InvalidOperationException(
                $"Kalendarze rodzaju {zrodlo.Kind} są tylko do odczytu.");

        return (zrodlo, pisarz);
    }

    /// <summary>Strefa, w której rysowana jest siatka. Na ekran, nie do liczenia.</summary>
    /// <remarks>
    /// Widoczna, bo „wszystkie godziny o dwie za wcześnie" i „pobrało się nie to"
    /// wyglądają na siatce identycznie, a są to dwie zupełnie różne rzeczy do zrobienia.
    /// </remarks>
    public string ZoneName => settings.Zone.Id;

    /// <summary>Co jest nie tak ze strefą, jeśli cokolwiek. Puste, gdy wszystko gra.</summary>
    public string? ZoneProblem => settings.ZoneProblem;

    public Task<IReadOnlyList<CalendarSource>> SourcesAsync(CancellationToken ct = default) =>
        store.SourcesAsync(ct);

    /// <summary>Wydarzenia w bazie w ogóle — do odróżnienia „nic nie ma" od „nie na te dni".</summary>
    public Task<int> StoredEventCountAsync(CancellationToken ct = default) =>
        store.CountAsync(ct);

    /// <summary>
    /// Podłączenie kalendarza. Do etapu 10 nie było **żadnej** drogi, żeby to zrobić:
    /// odświeżanie przechodziło po źródłach, których nikt nie umiał dodać.
    /// </summary>
    public async Task<CalendarSource> AddAsync(
        CalendarKind kind,
        string externalId,
        string name,
        string? color = null,
        CancellationToken ct = default)
    {
        // Ten sam kalendarz dwa razy to zawsze pomyłka — najczęściej klikanie „Dodaj"
        // w reakcji na to, że nic się nie pojawiło. Duplikaty mnożą potem te same
        // błędy w raporcie i zaciemniają jedyny, który coś znaczy.
        var szukany = externalId?.Trim() ?? string.Empty;

        if ((await store.SourcesAsync(ct)).FirstOrDefault(
                z => z.Kind == kind && z.ExternalId == szukany) is { } juzJest)
        {
            return juzJest;
        }

        var zrodlo = new CalendarSource(
            Guid.CreateVersion7(), clock.Now, hlc.Next(), kind, szukany, name, color);

        store.AddSource(zrodlo);
        await store.SaveChangesAsync(ct);

        return zrodlo;
    }

    /// <summary>Odłączenie. Nagrobek, nie usunięcie — wybór kalendarzy się synchronizuje.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        if ((await store.SourcesAsync(ct)).FirstOrDefault(z => z.Id == id) is not { } zrodlo)
        {
            return;
        }

        zrodlo.MarkDeleted(hlc.Next());
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Co ile odświeżać kanały (spec 10.1).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Odświeża źródła, którym minął czas. Zwraca też liczbę tych, które nie odpowiedziały.
    /// </summary>
    /// <remarks>
    /// Awaria jednego kanału nie może zatrzymać pozostałych ani wywrócić startu aplikacji.
    /// Kalendarz jest dodatkiem do zadań; niedostępny kanał ma znaczyć „brak świeżych
    /// wydarzeń", a nie „aplikacja się nie otwiera".
    /// </remarks>
    public async Task<CalendarRefreshReport> RefreshAsync(
        bool force = false, CancellationToken ct = default)
    {
        var teraz = clock.Now;
        var odswiezone = 0;
        var wydarzen = 0;
        var nieudane = 0;
        var powody = new List<string>();

        foreach (var zrodlo in await store.SourcesAsync(ct))
        {
            var kursor = await store.CursorAsync(zrodlo.Id, ct);

            if (!force && kursor is not null && teraz - kursor.FetchedAt < RefreshInterval)
            {
                continue;
            }

            if (feeds.FirstOrDefault(f => f.Kind == zrodlo.Kind) is not { } kanal)
            {
                // Rodzaj bez podłączonego kanału. Do dziś było to ciche pominięcie
                // i właśnie ono sprawiało, że kalendarz Google wyglądał na pusty.
                nieudane++;
                powody.Add($"{zrodlo.Name}: brak obsługi kalendarzy rodzaju {zrodlo.Kind}.");
                continue;
            }

            try
            {
                var wynik = await kanal.FetchAsync(zrodlo, kursor?.SyncToken, ct);

                await store.UpsertAsync(zrodlo.Id, wynik.Events, ct);

                if (wynik.IsFull)
                {
                    await store.MarkMissingCancelledAsync(
                        zrodlo.Id, wynik.Events.Select(e => e.ExternalId).ToList(), ct);
                }

                // Barwa dociągana przy każdym pobraniu, nie tylko przy podłączaniu.
                // Inaczej kalendarze podłączone przed wprowadzeniem barw zostałyby
                // szare na zawsze, a jedyną drogą byłoby odłączenie ich i dodanie od
                // nowa — czyli kazanie komuś naprawiać ręką coś, co aplikacja wie.
                // Puste znaczy „źródło nie mówi", więc nie kasuje barwy już zapisanej.
                // Porównanie przed zapisem, bo inaczej każde odświeżenie na każdym
                // urządzeniu dopisywałoby tę samą zmianę do dziennika synchronizacji.
                if (!string.IsNullOrWhiteSpace(wynik.Color) && wynik.Color != zrodlo.Color)
                {
                    zrodlo.SetColor(wynik.Color, hlc.Next());
                }

                store.SaveCursor(zrodlo.Id, wynik.SyncToken, teraz);
                await store.SaveChangesAsync(ct);

                odswiezone++;
                wydarzen += wynik.Events.Count;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                nieudane++;
                powody.Add($"{zrodlo.Name}: {e.Message}");
            }
        }

        return new CalendarRefreshReport(odswiezone, wydarzen, nieudane, powody);
    }

    /// <summary>
    /// Siatka na zadany zakres dni: wydarzenia z kalendarzy plus zadania.
    /// </summary>
    /// <remarks>
    /// Zadania lądują na tej samej siatce, ale półprzezroczyste (spec 11): wydarzenie
    /// jest umową z kimś, zadanie zamiarem wobec siebie, i te dwie rzeczy nie mogą
    /// wyglądać tak samo.
    /// </remarks>
    public async Task<IReadOnlyList<AgendaDay>> AgendaAsync(
        DateOnly from, int days, CancellationToken ct = default)
    {
        // Strefa, nie przesunięcie.
        //
        // Do dziś szło tu `clock.Now.Offset` — przesunięcie obowiązujące **w tej chwili**
        // — i było kładzione na każde wydarzenie niezależnie od jego daty. Po ostatniej
        // niedzieli października Polska ma +1, a nie +2: wszystko oglądane spoza
        // bieżącej zmiany czasu rysowało się i podpisywało godzinę obok. Przesunięcie
        // jest cechą chwili, nie kalendarza, więc liczy się je dla każdej chwili osobno.
        var strefa = settings.Zone;
        var poczatek = WStrefie(from.ToDateTime(TimeOnly.MinValue), strefa);
        var koniec = WStrefie(from.AddDays(days).ToDateTime(TimeOnly.MinValue), strefa);

        var wpisy = new List<AgendaEntry>();

        // Barwa jest cechą kalendarza, nie wydarzenia: przy jedenastu podłączonych
        // kalendarzach to jedyna rzecz, po której widać, do którego z nich coś należy.
        var zrodla = await store.SourcesAsync(ct);
        var barwy = zrodla.ToDictionary(z => z.Id, z => z.Color);

        // Do których kalendarzy umiemy pisać. Bez tego okno pokazywałoby pole wyboru
        // przy wydarzeniu z kanału iCal, czyli przycisk bez żadnego skutku.
        var zapisywalne = zrodla
            .Where(z => writers.Any(w => w.Kind == z.Kind))
            .Select(z => z.Id)
            .ToHashSet();

        // Zadania wczytane **przed** wydarzeniami, bo to one rozstrzygają, czego nie
        // rysować. Zadanie udostępnione ma w kalendarzu swoje odbicie, które wraca do
        // nas przy odświeżaniu — narysowane obok zadania dałoby dwa bloki na tę samą
        // rzecz, w tym samym miejscu, z których jeden nie dawałby się odhaczyć.
        // Prawdą jest zadanie; wydarzenie jest jego cieniem.
        var zadania = await tasks.UpcomingAsync(from.AddDays(-1), from.AddDays(days), ct);

        var odbicia = zadania
            .Where(z => z.SharedEventId is not null)
            .Select(z => z.SharedEventId!)
            .ToHashSet();

        foreach (var wydarzenie in await store.EventsAsync(poczatek, koniec, ct))
        {
            if (odbicia.Contains(wydarzenie.ExternalId))
            {
                continue;
            }

            // Całodniowe **nie** przelicza się na strefę. To jest data, nie chwila:
            // Google oddaje ją jako północ bez strefy, a przeliczenie na Warszawę robiło
            // z niej drugą w nocy — czyli koniec wypadał drugiej w nocy **następnego**
            // dnia i wpis rozlewał się na dwa dni. Pełnia widoczna w Google na piątek
            // stała u nas na piątku i sobocie.
            // Ptaszek zdejmowany z nazwy przy rysowaniu: jest stanem, nie częścią nazwy.
            // Zostawiony w tytule stałby obok kwadracika jako drugi ptaszek, a przy
            // zmianie nazwy w oknie szczegółu wróciłby do Google zapisany dwa razy.
            wpisy.Add(new AgendaEntry(
                EventMark.Strip(wydarzenie.Title),
                wydarzenie.IsAllDay
                    ? wydarzenie.StartsAt
                    : TimeZoneInfo.ConvertTime(wydarzenie.StartsAt, strefa),
                wydarzenie.IsAllDay
                    ? wydarzenie.EndsAt
                    : TimeZoneInfo.ConvertTime(wydarzenie.EndsAt, strefa),
                wydarzenie.IsAllDay,
                AgendaKind.Event,
                barwy.GetValueOrDefault(wydarzenie.SourceId),
                TaskId: null,
                wydarzenie.SourceId,
                wydarzenie.ExternalId,
                IsDone: EventMark.IsDone(wydarzenie.Title),
                CanWrite: zapisywalne.Contains(wydarzenie.SourceId)));
        }

        // Barwy dziedziczone w dół: zadanie bierze swoją, a gdy jej nie ma — projektu,
        // a gdy i tego nie ma — obszaru. Ustawienie koloru raz na obszarze koloruje
        // więc wszystko, co do niego należy, bez dotykania pojedynczych zadań.
        var barwyObszarow = (await areas.AllAsync(ct))
            .ToDictionary(o => o.Id, o => o.Color);
        var barwyProjektow = BarwyProjektow(await projects.AllAsync(ct), barwyObszarow);

        foreach (var zadanie in zadania)
        {
            if (Entry(zadanie, strefa, Barwa(zadanie, barwyProjektow, barwyObszarow)) is { } wpis)
            {
                wpisy.Add(wpis);
            }
        }

        return Agenda.Build(wpisy, from, days);
    }

    /// <summary>
    /// Zadanie na siatce. Bez godziny ląduje na pasku całodniowym — zgadywanie godziny
    /// zrobiłoby z listy zadań kalendarz, w którym wszystko jest umówione, a to jest
    /// dokładnie ten rodzaj planowania, który się nie utrzymuje.
    /// </summary>
    /// <summary>Chwila lokalna w strefie, z przesunięciem obowiązującym **tego dnia**.</summary>
    private static DateTimeOffset WStrefie(DateTime lokalna, TimeZoneInfo strefa) =>
        new(lokalna, strefa.GetUtcOffset(lokalna));

    /// <summary>
    /// Barwa każdego projektu po rozwinięciu dziedziczenia: własna, rodzica, obszaru.
    /// </summary>
    /// <remarks>
    /// Rozwinięcie tutaj, a nie przy każdym zadaniu, bo podprojekt potrafi mieć kilku
    /// przodków, a zadań w tygodniu są setki. Przejście zabezpieczone licznikiem:
    /// po scaleniu dwóch urządzeń projekt umie stać się własnym przodkiem.
    /// </remarks>
    private static Dictionary<Guid, string?> BarwyProjektow(
        IReadOnlyList<Project> projekty,
        IReadOnlyDictionary<Guid, string?> obszary)
    {
        var wedlugId = projekty.ToDictionary(p => p.Id);
        var wynik = new Dictionary<Guid, string?>(projekty.Count);

        foreach (var projekt in projekty)
        {
            string? znaleziona = null;
            Project? biezacy = projekt;

            for (var krok = 0; krok < projekty.Count && biezacy is not null; krok++)
            {
                if (!string.IsNullOrWhiteSpace(biezacy.Color))
                {
                    znaleziona = biezacy.Color;
                    break;
                }

                biezacy = biezacy.ParentProjectId is { } rodzic
                    && wedlugId.TryGetValue(rodzic, out var wyzej)
                        ? wyzej
                        : null;
            }

            wynik[projekt.Id] = znaleziona
                ?? (obszary.TryGetValue(projekt.AreaId, out var zObszaru) ? zObszaru : null);
        }

        return wynik;
    }

    /// <summary>Barwa zadania: własna, projektu albo obszaru — w tej kolejności.</summary>
    private static string? Barwa(
        TaskItem task,
        IReadOnlyDictionary<Guid, string?> projekty,
        IReadOnlyDictionary<Guid, string?> obszary)
    {
        if (!string.IsNullOrWhiteSpace(task.Color))
        {
            return task.Color;
        }

        if (task.ProjectId is { } projekt
            && projekty.TryGetValue(projekt, out var zProjektu)
            && !string.IsNullOrWhiteSpace(zProjektu))
        {
            return zProjektu;
        }

        return task.AreaId is { } obszar && obszary.TryGetValue(obszar, out var zObszaru)
            ? zObszaru
            : null;
    }

    private static AgendaEntry? Entry(TaskItem task, TimeZoneInfo zone, string? barwa)
    {
        if (task.DoDate is not { } dzien)
        {
            return null;
        }

        if (task.DoTime is not { } godzina)
        {
            var poczatekDnia = WStrefie(dzien.ToDateTime(TimeOnly.MinValue), zone);
            return new AgendaEntry(
                task.Title, poczatekDnia, poczatekDnia.AddDays(1),
                IsAllDay: true, AgendaKind.Task, barwa, task.Id,
                SourceId: null, ExternalId: null, IsDone: task.State == TaskState.Done,
                CanWrite: true);
        }

        var start = WStrefie(dzien.ToDateTime(godzina), zone);
        var dlugosc = TimeSpan.FromMinutes(task.EstimatedMinutes ?? 30);

        return new AgendaEntry(
            task.Title, start, start + dlugosc, IsAllDay: false, AgendaKind.Task,
            barwa, task.Id, SourceId: null, ExternalId: null,
            IsDone: task.State == TaskState.Done, CanWrite: true);
    }
}
