using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Areas;
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
    int Sources, int Events, int Failed, IReadOnlyList<string> Problems, int Folded = 0)
{
    public CalendarRefreshReport(int sources, int events, int failed)
        : this(sources, events, failed, [])
    {
    }

    /// <summary>Ile powtórzonych podłączeń tego samego kalendarza odrzucono po drodze.</summary>
    /// <remarks>
    /// Osobno od porażek, bo to nie jest porażka — ale musi być widać, że się zdarzyło.
    /// Bez tego złożenie duplikatów byłoby cichym skasowaniem czegoś, co użytkownik
    /// widział na liście kalendarzy.
    /// </remarks>
    public int Folded { get; init; } = Folded;
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
            try
            {
                await pisarz.UpdateAsync(zrodlo, externalId, draft, ct);
            }
            catch (WydarzenieZniknelo)
            {
                await ZdejmijDuchaAsync(zrodlo.Id, externalId, ct);
                throw;
            }
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

        try
        {
            await pisarz.DeleteAsync(zrodlo, externalId, ct);
        }
        catch (WydarzenieZniknelo)
        {
            // Skasowanie czegoś, czego już nie ma, jest **wykonaniem** prośby, a nie
            // awarią: po drugiej stronie stan jest dokładnie ten, o który chodziło.
            // Zostaje zdjąć to u nas — co i tak dzieje się niżej.
        }

        // U nas nagrobek, nie usunięcie — tak samo jak wszędzie indziej w tym modelu.
        //
        // Po samym identyfikatorze wydarzenia, nie po parze ze źródłem. Nasza kopia
        // bywa zapisana pod **innym** wierszem podłączenia niż to, przez które właśnie
        // kasujemy: dwa wiersze na ten sam kalendarz Google robią się przy pierwszej
        // synchronizacji między urządzeniami i żyją do chwili złożenia. Kasowanie
        // trafiało wtedy u źródła, a u nas nie trafiało nigdzie — i wpis zostawał
        // na siatce jako cudze wydarzenie po zadaniu, którego już nie ma. Identyfikator
        // wydarzenia jest u Google jednoznaczny, więc szersze dopasowanie nie może
        // zdjąć niczego innego.
        var nasze = await store.EventsAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        var zdjete = false;

        foreach (var wydarzenie in nasze.Where(e => e.ExternalId == externalId))
        {
            await store.UpsertAsync(
                wydarzenie.SourceId,
                [new FeedEvent(
                    externalId, wydarzenie.Title, wydarzenie.StartsAt, wydarzenie.EndsAt,
                    wydarzenie.IsAllDay, wydarzenie.Location, Cancelled: true)],
                ct);

            zdjete = true;
        }

        if (zdjete)
        {
            await store.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Pokazanie wydarzenia jednej osobie — dopisanie jej do gości.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Druga z dwóch dróg, którymi Google pokazuje komuś coś ze swojego kalendarza,
    /// i jedyna działająca na pojedynczej rzeczy. Pierwsza — udostępnienie całego
    /// kalendarza — znaczy „ta półka jest nasza wspólna" i po naszej stronie załatwia
    /// ją przypisanie kalendarza do obszaru. Ta znaczy „spójrz na to jedno".
    /// </para>
    /// <para>
    /// Nie ma tu trzeciego mechanizmu i nie powinno być. Własna lista „komu pokazane"
    /// byłaby drugim stanem mówiącym o tej samej rzeczy w miejscu, w którym Google
    /// ma już swój — i pierwsza zmiana zrobiona przez kogoś w jego kalendarzu
    /// rozjechałaby oba.
    /// </para>
    /// </remarks>
    public async Task<bool> InviteAsync(
        Guid sourceId, string externalId, string email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var (zrodlo, pisarz) = await DoZapisuAsync(sourceId, ct);

        return await pisarz.InviteAsync(zrodlo, externalId, email, ct);
    }

    /// <summary>Obszary, które mają przypisany kalendarz — razem z tym kalendarzem.</summary>
    public async Task<IReadOnlyList<Area>> AreasWithCalendarAsync(CancellationToken ct = default)
    {
        var zywe = (await store.SourcesAsync(ct)).Select(z => z.Id).ToHashSet();

        return (await areas.AllAsync(ct))
            .Where(o => !o.Deleted && o.IsActive && o.CalendarId is { } k && zywe.Contains(k))
            .OrderBy(o => o.SortOrder)
            .ToList();
    }

    /// <summary>Obszar, do którego należy ten kalendarz. Pusty, gdy żaden.</summary>
    public async Task<Area?> AreaOfCalendarAsync(Guid sourceId, CancellationToken ct = default) =>
        (await areas.AllAsync(ct)).FirstOrDefault(o => !o.Deleted && o.CalendarId == sourceId);

    /// <summary>
    /// Przeniesienie wydarzenia do innego kalendarza.
    /// </summary>
    /// <remarks>
    /// <para>
    /// U Google nie ma „przenieś": jest założenie w nowym i skasowanie w starym.
    /// Kolejność jest tu odwrotna niż przy przenoszeniu zadania i to jest rozstrzygnięcie,
    /// nie niedopatrzenie. Przy zadaniu prawdą jest zadanie, więc nieudane założenie
    /// w nowym miejscu da się powtórzyć z tego, co i tak mamy — dlatego tam idzie
    /// najpierw skasowanie, żeby nie zostały dwa wpisy. Tutaj prawdą jest wydarzenie,
    /// a nasza kopia jest kopią: nieudane założenie po skasowaniu znaczyłoby cudzy wpis
    /// skasowany bezpowrotnie. Duplikat jest widoczny i daje się usunąć jednym ruchem,
    /// a wpis, którego nie ma, nie daje się zauważyć.
    /// </para>
    /// <para>
    /// Oddaje identyfikator wpisu w nowym kalendarzu — u Google jest inny, bo to jest
    /// inne wydarzenie w innym kalendarzu, nie to samo przestawione.
    /// </para>
    /// </remarks>
    public async Task<string> MoveEventAsync(
        Guid sourceId, string externalId, Guid targetId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        if (sourceId == targetId)
        {
            return externalId;
        }

        var nasze = await store.EventsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        if (nasze.FirstOrDefault(e => e.SourceId == sourceId && e.ExternalId == externalId)
            is not { } wydarzenie)
        {
            throw new WydarzenieZniknelo(
                "Tego wydarzenia nie ma już w naszej kopii — odśwież kalendarz i spróbuj jeszcze raz.");
        }

        var nowy = await SaveEventAsync(
            targetId,
            externalId: null,
            new CalendarDraft(
                wydarzenie.Title, wydarzenie.StartsAt, wydarzenie.EndsAt,
                wydarzenie.Location, wydarzenie.IsAllDay),
            ct);

        await DeleteEventAsync(sourceId, externalId, ct);

        return nowy;
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

        try
        {
            await pisarz.RenameAsync(zrodlo, externalId, nazwa, ct);
        }
        catch (WydarzenieZniknelo)
        {
            await ZdejmijDuchaAsync(sourceId, externalId, ct);
            throw;
        }

        await store.UpsertAsync(
            sourceId,
            [new FeedEvent(
                externalId, nazwa, wydarzenie.StartsAt, wydarzenie.EndsAt,
                wydarzenie.IsAllDay, wydarzenie.Location, Cancelled: false)],
            ct);

        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Zdjęcie z siatki wydarzenia, którego nie ma już w Google.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skasowane po drugiej stronie — w telefonie, w przeglądarce, przez kogoś, z kim
    /// kalendarz jest dzielony. Nasza kopia jest wtedy duchem: widać ją, bo pobraliśmy
    /// ją, gdy jeszcze istniała, a każdy zapis wraca odmową.
    /// </para>
    /// <para>
    /// Zdejmowane od razu, przy pierwszej odmowie, a nie przy najbliższym pełnym
    /// odczycie. Odczyt pełny bywa za godzinę, a przez tę godzinę widać wpis, którego
    /// nie ma, i dostaje się odmowę za każdym razem, gdy się go dotknie. Jedna
    /// odpowiedź z Google wystarczy, żeby wiedzieć — nie ma po co czekać na drugą.
    /// </para>
    /// <para>
    /// Nagrobek, nie usunięcie: tak samo jak wszędzie indziej w tym modelu.
    /// </para>
    /// </remarks>
    private async Task ZdejmijDuchaAsync(Guid sourceId, string externalId, CancellationToken ct)
    {
        var nasze = await store.EventsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        if (nasze.FirstOrDefault(e => e.SourceId == sourceId && e.ExternalId == externalId)
            is not { } duch)
        {
            return;
        }

        await store.UpsertAsync(
            sourceId,
            [new FeedEvent(
                externalId, duch.Title, duch.StartsAt, duch.EndsAt,
                duch.IsAllDay, duch.Location, Cancelled: true)],
            ct);

        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Przepięcie kalendarza głównego, jeśli wskazuje na odrzucone podłączenie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Składanie duplikatów przepina to, co widzi w chwili składania. Nie wystarczy:
    /// wskazanie bywa <b>starsze</b> od poprawki albo przyjeżdża synchronizacją później.
    /// Aplikacja, w której złożenie już się odbyło przed tą poprawką, zostałaby ze
    /// wskazaniem wiszącym na zawsze — a objawem byłaby odmowa zapisu przy kalendarzach
    /// wyświetlających się poprawnie.
    /// </para>
    /// <para>
    /// Dlatego naprawa przy każdym odświeżeniu, nie tylko przy składaniu. Kosztuje jedno
    /// porównanie, gdy nie ma czego naprawiać, i wykonuje się sama u kogoś, kto o tej
    /// usterce nigdy się nie dowie.
    /// </para>
    /// </remarks>
    private async Task NaprawGlownyAsync(CancellationToken ct)
    {
        if (settings.MainCalendarId is not { } glowny)
        {
            return;
        }

        var zywy = await ZywyKalendarzAsync(glowny, ct);

        if (zywy is { } id && id != glowny)
        {
            settings.SetMainCalendar(id);
        }
    }

    /// <summary>
    /// Żyjące podłączenie, na które wskazuje ten identyfikator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten sam kalendarz Google podłączony na dwóch urządzeniach dostaje na każdym
    /// <b>własny</b> identyfikator wiersza — to identyfikator zewnętrzny jest wspólny,
    /// nie nasz. Wybór kalendarza się synchronizuje, więc zadanie udostępnione na
    /// komputerze przyjeżdża na telefon ze wskazaniem na wiersz komputera; składanie
    /// duplikatów zostawia z tej pary jeden i drugi staje się nagrobkiem.
    /// </para>
    /// <para>
    /// Wskazanie na nagrobek nie jest wtedy błędem, tylko starym imieniem tej samej
    /// rzeczy. Odmowa zapisu przy takim wskazaniu wyglądała z zewnątrz absurdalnie:
    /// wszystkie kalendarze wyświetlały się poprawnie, a zadania z drugiego urządzenia
    /// nie dawały się ani zapisać, ani skasować.
    /// </para>
    /// <para>
    /// Puste znaczy „tego podłączenia naprawdę nie ma" — odłączono je ręcznie i nic
    /// nie zajęło jego miejsca. To jedyny przypadek, w którym zapis ma odmówić.
    /// </para>
    /// </remarks>
    public async Task<Guid?> ZywyKalendarzAsync(Guid sourceId, CancellationToken ct = default)
    {
        var zywe = await store.SourcesAsync(ct);

        if (zywe.Any(z => z.Id == sourceId))
        {
            return sourceId;
        }

        if ((await store.AllSourcesAsync(ct)).FirstOrDefault(z => z.Id == sourceId)
            is not { } nagrobek)
        {
            return null;
        }

        return zywe.FirstOrDefault(
                z => z.Kind == nagrobek.Kind && z.ExternalId == nagrobek.ExternalId)?.Id;
    }

    private async Task<(CalendarSource Source, ICalendarWriter Writer)> DoZapisuAsync(
        Guid sourceId, CancellationToken ct)
    {
        // Rozstrzygnięcie przed odmową: wskazanie na odrzucony duplikat jest starym
        // imieniem żyjącego podłączenia, a nie brakiem kalendarza.
        var rozstrzygniete = await ZywyKalendarzAsync(sourceId, ct);

        var zrodlo = rozstrzygniete is { } id
            ? (await store.SourcesAsync(ct)).First(z => z.Id == id)
            : throw new InvalidOperationException(
                "Tego kalendarza już nie ma na liście podłączonych. "
                + "Wybierz kalendarz główny w Ustawieniach → Kalendarze.");

        var pisarz = writers.FirstOrDefault(w => w.Kind == zrodlo.Kind)
            ?? throw new InvalidOperationException(
                $"Kalendarze rodzaju {zrodlo.Kind} są tylko do odczytu.");

        return (zrodlo, pisarz);
    }

    /// <summary>
    /// Barwa wydarzenia: obszaru, do którego należy jego kalendarz, a w braku — kalendarza.
    /// </summary>
    /// <remarks>
    /// Obszar wygrywa, bo to on jest podziałem. Barwa kalendarza zostaje dla wszystkiego,
    /// czego nikt do żadnego obszaru nie przypisał — świąt, wywiadówek, kanałów, które
    /// się tylko czyta.
    /// </remarks>
    private static string? BarwaWydarzenia(
        Guid sourceId,
        IReadOnlyDictionary<Guid, Area> obszary,
        IReadOnlyDictionary<Guid, string?> barwyZrodel) =>
        obszary.TryGetValue(sourceId, out var obszar) && obszar.Color is { } barwa
            ? barwa
            : barwyZrodel.GetValueOrDefault(sourceId);

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
    /// <remarks>
    /// <para>
    /// Godzina brała się z czasów, gdy kalendarz zewnętrzny był tłem dla zadań: coś,
    /// na co się zerka, żeby nie zaplanować spotkania na spotkaniu. Odkąd wydarzenie
    /// i zadanie mają być tą samą rzeczą pod ręką, godzina znaczy, że połowa tej samej
    /// rzeczy dociera w kilkanaście sekund, a druga połowa po godzinie — i że wpis
    /// dodany na komputerze nie istnieje na telefonie przez cały wieczór.
    /// </para>
    /// <para>
    /// Pięć minut, bo pobranie przyrostowe jest tanie: znacznik z poprzedniego odczytu
    /// sprawia, że Google oddaje samą różnicę, a najczęściej pustą. To jedno małe
    /// zapytanie na kalendarz.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

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

        var zlozone = await ZlozDuplikatyAsync(ct);

        await NaprawGlownyAsync(ct);

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

        return new CalendarRefreshReport(odswiezone, wydarzen, nieudane, powody, zlozone);
    }

    /// <summary>
    /// Odrzucenie powtórzonych podłączeń tego samego kalendarza.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Skąd się biorą.</b> Podłączenie kalendarza jest decyzją, więc się synchronizuje;
    /// same wydarzenia nie — każde urządzenie pobiera je sobie. Pilnowanie duplikatów
    /// siedziało wyłącznie w <see cref="AddAsync"/>, czyli na drodze ręcznej. Telefon,
    /// który podłączył swój kalendarz **zanim** doszło do niego podłączenie z komputera,
    /// dostawał drugi wiersz na ten sam kalendarz — tą samą drogą, na której nikt nie
    /// pytał, czy taki już jest.
    /// </para>
    /// <para>
    /// Objaw nie wyglądał na duplikat źródła: odświeżanie przechodzi po źródłach, więc
    /// ten sam kalendarz pobierał się dwa razy, a klucz kopii to para źródło–identyfikator
    /// zewnętrzny. Każde wydarzenie miało więc dwa wiersze i siatka rysowała wszystko
    /// podwójnie, obok siebie, w tych samych barwach. Wyglądało to na błąd rysowania.
    /// </para>
    /// <para>
    /// Zostaje **najstarsze** podłączenie — po dacie założenia, a przy jej remisie po
    /// identyfikatorze. Obie te rzeczy jadą razem ze źródłem, więc każde urządzenie
    /// wybiera to samo bez uzgadniania; sam identyfikator by nie wystarczył, bo dwa
    /// założone w tej samej milisekundzie są względem siebie nieuporządkowane.
    /// Odrzucenie jest nagrobkiem, czyli dojdzie i do drugiej strony.
    /// </para>
    /// </remarks>
    private async Task<int> ZlozDuplikatyAsync(CancellationToken ct)
    {
        var powtorzone = (await store.SourcesAsync(ct))
            .GroupBy(z => (z.Kind, z.ExternalId))
            .Where(g => g.Count() > 1)
            .ToList();

        if (powtorzone.Count == 0)
        {
            return 0;
        }

        var zlozone = 0;
        var wszystkie = await tasks.AllAsync(ct);
        var obszary = await areas.AllAsync(ct);

        foreach (var grupa in powtorzone)
        {
            var zostaje = grupa.OrderBy(z => z.CreatedAt).ThenBy(z => z.Id).First();

            foreach (var nadmiarowe in grupa.Where(z => z.Id != zostaje.Id))
            {
                nadmiarowe.MarkDeleted(hlc.Next());

                // Kopia wydarzeń odrzuconego źródła do skasowania: jest lokalna i nikomu
                // już niepotrzebna, a policzona w „ile w bazie" myliłaby przy szukaniu
                // dokładnie tej usterki.
                await store.ForgetEventsAsync(nadmiarowe.Id, ct);

                Przepnij(nadmiarowe.Id, zostaje.Id, wszystkie, obszary);
                zlozone++;
            }
        }

        await store.SaveChangesAsync(ct);

        return zlozone;
    }

    /// <summary>
    /// Przepięcie tego, co wskazywało na odrzucone podłączenie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bez tego złożenie duplikatów zostawiało wiszące wskazania i objawiało się dopiero
    /// przy zapisie: „tego kalendarza już nie ma na liście podłączonych" przy zadaniu,
    /// którego nikt nie ruszał, w aplikacji pokazującej wszystkie kalendarze poprawnie.
    /// Odczyt szedł przez to, co zostało, a zapis przez to, co zniknęło.
    /// </para>
    /// <para>
    /// Przepięcie jest bezpieczne, bo obie strony wskazują <b>ten sam</b> kalendarz
    /// u źródła — na tym polega bycie duplikatem. Identyfikator wydarzenia w Google
    /// zostaje ten sam i dalej jest ważny.
    /// </para>
    /// </remarks>
    private void Przepnij(
        Guid odrzucone,
        Guid zostaje,
        IReadOnlyList<TaskItem> zadania,
        IReadOnlyList<Area> obszary)
    {
        if (settings.MainCalendarId == odrzucone)
        {
            settings.SetMainCalendar(zostaje);
        }

        // Obszary też, bo od nich zależy, czyje jest wydarzenie. Wskazanie na odrzucony
        // wiersz znaczyłoby obszar bez kalendarza i kalendarz bez obszaru — czyli
        // wydarzenia, które z dnia na dzień przestają do czegokolwiek należeć.
        foreach (var obszar in obszary.Where(o => o.CalendarId == odrzucone))
        {
            obszar.SetCalendar(zostaje, hlc.Next());
        }

        // Z identyfikatorem wydarzenia, bo bez niego nie ma czego przepinać: udostępnienie
        // ustawia obie rzeczy naraz i jedna bez drugiej znaczy zadanie, którego w Google
        // nie ma.
        foreach (var zadanie in zadania.Where(
            z => z.SharedCalendarId == odrzucone && z.SharedEventId is not null))
        {
            zadanie.Share(zostaje, zadanie.SharedEventId!, hlc.Next());
        }
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

        // Obszary wczytane raz, na dwie rzeczy naraz: barwę zadań i przynależność
        // wydarzeń. Kalendarz przypisany do obszaru **jest** tym obszarem, więc
        // wydarzenie stamtąd ma wyglądać jak wszystko inne z tego obszaru — inaczej
        // odbiór dziecka wpisany w Google i odbiór dziecka wpisany w Marshalu stałyby
        // obok siebie w dwóch kolorach, choć są tą samą rzeczą z tej samej półki.
        var wszystkieObszary = await areas.AllAsync(ct);

        // Nagrobki odsiane, a przy sklejce zostaje pierwszy: przypisanie jest jeden
        // do jednego, ale dwa nagrobki po przenoszeniu mogą wskazywać ten sam kalendarz.
        var obszaryKalendarzy = wszystkieObszary
            .Where(o => !o.Deleted && o.CalendarId is not null)
            .GroupBy(o => o.CalendarId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

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
        var umowione = await tasks.UpcomingAsync(from.AddDays(-1), from.AddDays(days), ct);

        // Wybory na dzień dołożone do umówionych. Zadanie wzięte „na dziś" w widoku
        // „Teraz" nie dostaje dnia wykonania — dostaje obietnicę daną sobie — więc
        // w kalendarzu nie było go widać wcale. A kalendarz jest jedynym miejscem,
        // w którym widać cały dzień naraz, i obietnica należy do niego tak samo jak
        // spotkanie. Ląduje na pasku całodniowym, bo godziny nie ma i zgadywanie jej
        // zrobiłoby z listy zadań kalendarz, w którym wszystko jest umówione.
        var wybrane = await tasks.FocusedBetweenAsync(from, from.AddDays(days), ct);

        var zadania = umowione
            .Concat(wybrane.Where(w => umowione.All(u => u.Id != w.Id)))
            .ToList();

        // Wskazania **wszystkich** zadań, nie tylko tych widocznych w tym zakresie.
        //
        // Objaw, który to wymusił: zadanie zdjęte z dzisiejszego dnia zostawiało na
        // sekundę albo dwie swoje odbicie z Google, narysowane jako całodniowy pasek
        // przez cały dzień. Zadanie znikało z siatki natychmiast, a jego cień żył do
        // chwili, gdy zdjęcie odbicia wróciło z sieci — i przez tę chwilę nic już go
        // nie odsiewało, bo odsiew szedł po zadaniach **z oglądanego zakresu**, a tego
        // zadania w nim właśnie zabrakło.
        //
        // Wskazanie znika dopiero wtedy, gdy zdjęcie odbicia doszło do skutku, więc
        // przez cały ten czas cień pozostaje cieniem. Gdyby zdjęcie nie doszło nigdy,
        // dokańczanie zaległych kasowań próbuje co minutę i zapisuje powód w dzienniku
        // — wpis nie znika więc po cichu, tylko czeka na skutek.
        var odbicia = (await tasks.MirroredEventIdsAsync(ct)).ToHashSet(StringComparer.Ordinal);

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
                BarwaWydarzenia(wydarzenie.SourceId, obszaryKalendarzy, barwy),
                TaskId: null,
                wydarzenie.SourceId,
                wydarzenie.ExternalId,
                IsDone: EventMark.IsDone(wydarzenie.Title),
                CanWrite: zapisywalne.Contains(wydarzenie.SourceId)));
        }

        // Barwy dziedziczone w dół: zadanie bierze swoją, a gdy jej nie ma — projektu,
        // a gdy i tego nie ma — obszaru. Ustawienie koloru raz na obszarze koloruje
        // więc wszystko, co do niego należy, bez dotykania pojedynczych zadań.
        var barwyObszarow = wszystkieObszary.ToDictionary(o => o.Id, o => o.Color);
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
        // Dzień wykonania, a gdy go nie ma — dzień, na który zadanie zostało wybrane.
        // Kolejność nie jest dowolna: zadanie umówione na czwartek i wzięte na dziś
        // ma stać w czwartek, bo tam jest umówione. Wybór mówi „zajmę się tym", a nie
        // „to się wtedy odbywa".
        if ((task.DoDate ?? task.FocusDate) is not { } dzien)
        {
            return null;
        }

        if (task.DoDate is null || task.DoTime is not { } godzina)
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
