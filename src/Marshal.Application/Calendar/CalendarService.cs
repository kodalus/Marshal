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
    /// Czy do tego konkretnego podłączenia da się pisać.
    /// </summary>
    /// <remarks>
    /// Rodzaj nie wystarcza: dwa kalendarze Google stoją obok siebie, jeden własny,
    /// drugi świąteczny — i tylko do pierwszego wolno. Pytanie o sam rodzaj oddawało
    /// prawdę dla obu, więc świąteczny dawało się wybrać na kalendarz główny albo
    /// przypisać do obszaru; kończyło się to odmową przy pierwszym zadaniu z godziną.
    /// </remarks>
    public bool CanWrite(CalendarSource source) =>
        source is { ReadOnly: false } && CanWrite(source.Kind);

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

        var (source, writer) = await ForWriteAsync(sourceId, ct);

        var id = string.IsNullOrWhiteSpace(externalId)
            ? await writer.CreateAsync(source, draft, ct)
            : externalId;

        if (!string.IsNullOrWhiteSpace(externalId))
        {
            try
            {
                await writer.UpdateAsync(source, externalId, draft, ct);
            }
            catch (EventGone)
            {
                await DropGhostAsync(source.Id, externalId, ct);
                throw;
            }
        }

        // Całodniowość z brudnopisu, nie fałsz na sztywno.
        //
        // Stało tu „nie" dla każdego zapisu i to nie jest drobiazg wyglądu. Wydarzenie
        // całodniowe ma u nas zapisane granice jako północ bez strefy; zapisane jako
        // godzinowe przelicza się przy rysowaniu na strefę okna i robi się z niego
        // bloczek od drugiej w nocy do drugiej w nocy **następnego dnia** — czyli wpis
        // rozlany na dwie doby, na pierwszej „02:00 – 00:00", na drugiej „00:00 – 02:00".
        //
        // Do Google jechało przy tym poprawnie, jako data bez godziny. Rozjeżdżała się
        // więc wyłącznie nasza kopia — i to najgorszym możliwym sposobem: przy każdym
        // odświeżeniu wracała z Google prawidłowa, a przy każdym zapisie psuła się
        // z powrotem.
        await store.UpsertAsync(
            source.Id,
            [new FeedEvent(
                id, draft.Title, draft.Start, draft.End,
                draft.AllDay, draft.Location, Cancelled: false)],
            ct);

        await store.SaveChangesAsync(ct);

        return id;
    }

    /// <summary>Skasowanie wydarzenia u źródła i u nas. Tylko to wskazane wprost.</summary>
    public async Task DeleteEventAsync(
        Guid sourceId, string externalId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        var (source, writer) = await ForWriteAsync(sourceId, ct);

        try
        {
            await writer.DeleteAsync(source, externalId, ct);
        }
        catch (EventGone)
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
        var ours = await store.EventsAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        var removed = false;

        foreach (var ev in ours.Where(e => e.ExternalId == externalId))
        {
            await store.UpsertAsync(
                ev.SourceId,
                [new FeedEvent(
                    externalId, ev.Title, ev.StartsAt, ev.EndsAt,
                    ev.IsAllDay, ev.Location, Cancelled: true)],
                ct);

            removed = true;
        }

        if (removed)
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

        var (source, writer) = await ForWriteAsync(sourceId, ct);

        return await writer.InviteAsync(source, externalId, email, ct);
    }

    /// <summary>Obszary, które mają przypisany kalendarz — razem z tym kalendarzem.</summary>
    public async Task<IReadOnlyList<Area>> AreasWithCalendarAsync(CancellationToken ct = default)
    {
        // Tylko te, do których wolno pisać: przeniesienie wydarzenia do obszaru znaczy
        // założenie go w kalendarzu tego obszaru. Obszar wskazujący kalendarz świąteczny
        // stałby na liście jako możliwy wybór i kończył się odmową po kliknięciu.
        var live = (await store.SourcesAsync(ct))
            .Where(CanWrite)
            .Select(z => z.Id)
            .ToHashSet();

        return (await areas.AllAsync(ct))
            .Where(o => !o.Deleted && o.IsActive && o.CalendarId is { } k && live.Contains(k))
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

        var ours = await store.EventsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        if (ours.FirstOrDefault(e => e.SourceId == sourceId && e.ExternalId == externalId)
            is not { } ev)
        {
            throw new EventGone(
                "Tego wydarzenia nie ma już w naszej kopii — odśwież kalendarz i spróbuj jeszcze raz.");
        }

        var created = await SaveEventAsync(
            targetId,
            externalId: null,
            new CalendarDraft(
                ev.Title, ev.StartsAt, ev.EndsAt,
                ev.Location, ev.IsAllDay),
            ct);

        try
        {
            await DeleteEventAsync(sourceId, externalId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nieudane zdjęcie ze starego kalendarza **cofa** założenie w nowym.
            //
            // Objaw, który to wymusił: kalendarz tylko do odczytu — świąteczny, fazy
            // księżyca, cudzy udostępniony bez prawa zmian. Założenie w nowym udawało
            // się, bo tam wolno pisać; skasowanie w starym wracało z odmową. Zostawała
            // odmowa na ekranie **i** kopia w kalendarzu, o którą nikt nie prosił.
            //
            // Rozumowanie z komentarza wyżej dalej jest ważne: przy awarii przejściowej
            // duplikat jest lepszy od skasowanego cudzego wpisu. Ale to nie była awaria
            // przejściowa, tylko brak prawa, którego powtórzenie nie zmieni — i wtedy
            // jedyna rzecz, jakiej nikt nie chciał, to właśnie ta kopia.
            try
            {
                await DeleteEventAsync(targetId, created, ct);
            }
            catch (Exception whenUndoing) when (whenUndoing is not OperationCanceledException)
            {
                // Nieudane cofnięcie **dopisuje się** do pierwotnego powodu, zamiast go
                // przykrywać. Powód mówi, czemu przeniesienie nie wyszło; dopisek mówi,
                // że została po nim kopia — a to dwie różne rzeczy do zrobienia.
                throw new InvalidOperationException(
                    $"{e.Message} Do tego kopia założona w nowym kalendarzu została na miejscu "
                    + $"i nie udało się jej zdjąć ({whenUndoing.Message}) — usuń ją ręcznie.",
                    e);
            }

            throw;
        }

        return created;
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

        var (source, writer) = await ForWriteAsync(sourceId, ct);

        var ours = await store.EventsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        if (ours.FirstOrDefault(e => e.SourceId == sourceId && e.ExternalId == externalId)
            is not { } ev)
        {
            throw new InvalidOperationException(
                "Tego wydarzenia nie ma już w pobranej kopii kalendarza. Odśwież i spróbuj raz jeszcze.");
        }

        var name = EventMark.Set(ev.Title, done);

        if (name == ev.Title)
        {
            return;
        }

        try
        {
            await writer.RenameAsync(source, externalId, name, ct);
        }
        catch (EventGone)
        {
            await DropGhostAsync(sourceId, externalId, ct);
            throw;
        }

        await store.UpsertAsync(
            sourceId,
            [new FeedEvent(
                externalId, name, ev.StartsAt, ev.EndsAt,
                ev.IsAllDay, ev.Location, Cancelled: false)],
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
    private async Task DropGhostAsync(Guid sourceId, string externalId, CancellationToken ct)
    {
        var ours = await store.EventsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);

        if (ours.FirstOrDefault(e => e.SourceId == sourceId && e.ExternalId == externalId)
            is not { } ghost)
        {
            return;
        }

        await store.UpsertAsync(
            sourceId,
            [new FeedEvent(
                externalId, ghost.Title, ghost.StartsAt, ghost.EndsAt,
                ghost.IsAllDay, ghost.Location, Cancelled: true)],
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
    private async Task FixPrimaryAsync(CancellationToken ct)
    {
        if (settings.MainCalendarId is not { } primary)
        {
            return;
        }

        var live = await LiveCalendarAsync(primary, ct);

        if (live is { } id && id != primary)
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
    public async Task<Guid?> LiveCalendarAsync(Guid sourceId, CancellationToken ct = default)
    {
        var live = await store.SourcesAsync(ct);

        if (live.Any(z => z.Id == sourceId))
        {
            return sourceId;
        }

        if ((await store.AllSourcesAsync(ct)).FirstOrDefault(z => z.Id == sourceId)
            is not { } tombstone)
        {
            return null;
        }

        return live.FirstOrDefault(
                z => z.Kind == tombstone.Kind
                  && z.ExternalId == tombstone.ExternalId
                  && z.Account == tombstone.Account)?.Id;
    }

    private async Task<(CalendarSource Source, ICalendarWriter Writer)> ForWriteAsync(
        Guid sourceId, CancellationToken ct)
    {
        // Rozstrzygnięcie przed odmową: wskazanie na odrzucony duplikat jest starym
        // imieniem żyjącego podłączenia, a nie brakiem kalendarza.
        var resolved = await LiveCalendarAsync(sourceId, ct);

        var source = resolved is { } id
            ? (await store.SourcesAsync(ct)).First(z => z.Id == id)
            : throw new InvalidOperationException(
                "Tego kalendarza już nie ma na liście podłączonych. "
                + "Wybierz kalendarz główny w Ustawieniach → Kalendarze.");

        var writer = writers.FirstOrDefault(w => w.Kind == source.Kind)
            ?? throw new InvalidOperationException(
                $"Kalendarze rodzaju {source.Kind} są tylko do odczytu.");

        // Odmowa **przed** czynnością, nie po niej. Google odpowiada na to samo swoim
        // 403, ale dopiero po wykonaniu wszystkiego, co przed — a przy przenoszeniu
        // wydarzenia „wszystko, co przed" znaczy kopię założoną w nowym kalendarzu.
        if (source.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Kalendarz „{source.Name}” jest udostępniony tylko do odczytu — "
                + "nie wolno w nim nic zmieniać ani kasować. Tak są ustawione kalendarze "
                + "świąteczne, fazy księżyca i cudze udostępnione bez prawa zmian.");
        }

        return (source, writer);
    }

    /// <summary>
    /// Barwa wydarzenia: obszaru, do którego należy jego kalendarz, a w braku — kalendarza.
    /// </summary>
    /// <remarks>
    /// Obszar wygrywa, bo to on jest podziałem. Barwa kalendarza zostaje dla wszystkiego,
    /// czego nikt do żadnego obszaru nie przypisał — świąt, wywiadówek, kanałów, które
    /// się tylko czyta.
    /// </remarks>
    private static string? EventColor(
        Guid sourceId,
        IReadOnlyDictionary<Guid, Area> areas,
        IReadOnlyDictionary<Guid, string?> sourceColors) =>
        areas.TryGetValue(sourceId, out var area) && area.Color is { } color
            ? color
            : sourceColors.GetValueOrDefault(sourceId);

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
        string? account = null,
        bool readOnly = false,
        CancellationToken ct = default)
    {
        // Ten sam kalendarz dwa razy to zawsze pomyłka — najczęściej klikanie „Dodaj"
        // w reakcji na to, że nic się nie pojawiło. Duplikaty mnożą potem te same
        // błędy w raporcie i zaciemniają jedyny, który coś znaczy.
        //
        // Z kontem w porównaniu: ten sam identyfikator kalendarza potrafi wystąpić
        // na dwóch kontach — udostępniony widnieje u obu stron pod tym samym adresem
        // — a to są wtedy dwa różne podłączenia, o różnych uprawnieniach.
        var wanted = externalId?.Trim() ?? string.Empty;
        var normalized = string.IsNullOrWhiteSpace(account) ? null : account.Trim();

        if ((await store.SourcesAsync(ct)).FirstOrDefault(
                z => z.Kind == kind && z.ExternalId == wanted && z.Account == normalized)
            is { } alreadyThere)
        {
            return alreadyThere;
        }

        var source = new CalendarSource(
            Guid.CreateVersion7(), clock.Now, hlc.Next(), kind, wanted, name, color, normalized);

        // Poziom dostępu znany już przy podłączaniu — lista kalendarzy z konta podaje
        // go razem z nazwą i barwą. Bez tego kalendarz świąteczny wyglądałby na
        // zapisywalny aż do pierwszego odświeżenia, czyli akurat przez te kilka chwil,
        // w których człowiek go ogląda po dodaniu.
        source.SetReadOnly(readOnly);

        store.AddSource(source);
        await store.SaveChangesAsync(ct);

        return source;
    }

    /// <summary>Odłączenie. Nagrobek, nie usunięcie — wybór kalendarzy się synchronizuje.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        if ((await store.SourcesAsync(ct)).FirstOrDefault(z => z.Id == id) is not { } source)
        {
            return;
        }

        source.MarkDeleted(hlc.Next());
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
        var now = clock.Now;
        var refreshed = 0;
        var events = 0;
        var failed = 0;
        var reasons = new List<string>();

        var merged = await MergeDuplicatesAsync(ct);

        await FixPrimaryAsync(ct);

        foreach (var source in await store.SourcesAsync(ct))
        {
            var cursor = await store.CursorAsync(source.Id, ct);

            if (!force && cursor is not null && now - cursor.FetchedAt < RefreshInterval)
            {
                continue;
            }

            if (feeds.FirstOrDefault(f => f.Kind == source.Kind) is not { } feed)
            {
                // Rodzaj bez podłączonego kanału. Do dziś było to ciche pominięcie
                // i właśnie ono sprawiało, że kalendarz Google wyglądał na pusty.
                failed++;
                reasons.Add($"{source.Name}: brak obsługi kalendarzy rodzaju {source.Kind}.");
                continue;
            }

            try
            {
                var result = await feed.FetchAsync(source, cursor?.SyncToken, ct);

                await store.UpsertAsync(source.Id, result.Events, ct);

                if (result.IsFull)
                {
                    await store.MarkMissingCancelledAsync(
                        source.Id, result.Events.Select(e => e.ExternalId).ToList(), ct);
                }

                // Barwa dociągana przy każdym pobraniu, nie tylko przy podłączaniu.
                // Inaczej kalendarze podłączone przed wprowadzeniem barw zostałyby
                // szare na zawsze, a jedyną drogą byłoby odłączenie ich i dodanie od
                // nowa — czyli kazanie komuś naprawiać ręką coś, co aplikacja wie.
                // Puste znaczy „źródło nie mówi", więc nie kasuje barwy już zapisanej.
                // Porównanie przed zapisem, bo inaczej każde odświeżenie na każdym
                // urządzeniu dopisywałoby tę samą zmianę do dziennika synchronizacji.
                if (!string.IsNullOrWhiteSpace(result.Color) && result.Color != source.Color)
                {
                    source.SetColor(result.Color, hlc.Next());
                }

                // Poziom dostępu przy każdym pobraniu, bo się zmienia: ktoś dopuszcza
                // do swojego kalendarza albo dostęp odbiera. Puste znaczy „źródło nie
                // mówi" — kanał iCal nie zna tego pojęcia i nie ma prawa nadpisywać
                // odpowiedzi, którą podał kto inny.
                if (result.ReadOnly is { } readOnly && readOnly != source.ReadOnly)
                {
                    source.SetReadOnly(readOnly);
                }

                store.SaveCursor(source.Id, result.SyncToken, now);
                await store.SaveChangesAsync(ct);

                refreshed++;
                events += result.Events.Count;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed++;
                reasons.Add($"{source.Name}: {e.Message}");
            }
        }

        return new CalendarRefreshReport(refreshed, events, failed, reasons, merged);
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
    private async Task<int> MergeDuplicatesAsync(CancellationToken ct)
    {
        var repeated = (await store.SourcesAsync(ct))
            .GroupBy(z => (z.Kind, z.ExternalId, z.Account))
            .Where(g => g.Count() > 1)
            .ToList();

        if (repeated.Count == 0)
        {
            return 0;
        }

        var merged = 0;
        var all = await tasks.AllAsync(ct);
        var allAreas = await areas.AllAsync(ct);

        foreach (var group in repeated)
        {
            var stays = group.OrderBy(z => z.CreatedAt).ThenBy(z => z.Id).First();

            foreach (var extra in group.Where(z => z.Id != stays.Id))
            {
                extra.MarkDeleted(hlc.Next());

                // Kopia wydarzeń odrzuconego źródła do skasowania: jest lokalna i nikomu
                // już niepotrzebna, a policzona w „ile w bazie" myliłaby przy szukaniu
                // dokładnie tej usterki.
                await store.ForgetEventsAsync(extra.Id, ct);

                Reattach(extra.Id, stays.Id, all, allAreas);
                merged++;
            }
        }

        await store.SaveChangesAsync(ct);

        return merged;
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
    private void Reattach(
        Guid rejected,
        Guid stays,
        IReadOnlyList<TaskItem> tasks,
        IReadOnlyList<Area> areas)
    {
        if (settings.MainCalendarId == rejected)
        {
            settings.SetMainCalendar(stays);
        }

        // Obszary też, bo od nich zależy, czyje jest wydarzenie. Wskazanie na odrzucony
        // wiersz znaczyłoby obszar bez kalendarza i kalendarz bez obszaru — czyli
        // wydarzenia, które z dnia na dzień przestają do czegokolwiek należeć.
        foreach (var area in areas.Where(o => o.CalendarId == rejected))
        {
            area.SetCalendar(stays, hlc.Next());
        }

        // Z identyfikatorem wydarzenia, bo bez niego nie ma czego przepinać: udostępnienie
        // ustawia obie rzeczy naraz i jedna bez drugiej znaczy zadanie, którego w Google
        // nie ma.
        foreach (var task in tasks.Where(
            z => z.SharedCalendarId == rejected && z.SharedEventId is not null))
        {
            task.Share(stays, task.SharedEventId!, hlc.Next());
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
        var zone = settings.Zone;
        var start = InZone(from.ToDateTime(TimeOnly.MinValue), zone);
        var end = InZone(from.AddDays(days).ToDateTime(TimeOnly.MinValue), zone);

        var entries = new List<AgendaEntry>();

        // Barwa jest cechą kalendarza, nie wydarzenia: przy jedenastu podłączonych
        // kalendarzach to jedyna rzecz, po której widać, do którego z nich coś należy.
        var sources = await store.SourcesAsync(ct);
        var colors = sources.ToDictionary(z => z.Id, z => z.Color);

        // Obszary wczytane raz, na dwie rzeczy naraz: barwę zadań i przynależność
        // wydarzeń. Kalendarz przypisany do obszaru **jest** tym obszarem, więc
        // wydarzenie stamtąd ma wyglądać jak wszystko inne z tego obszaru — inaczej
        // odbiór dziecka wpisany w Google i odbiór dziecka wpisany w Marshalu stałyby
        // obok siebie w dwóch kolorach, choć są tą samą rzeczą z tej samej półki.
        var allAreas = await areas.AllAsync(ct);

        // Nagrobki odsiane, a przy sklejce zostaje pierwszy: przypisanie jest jeden
        // do jednego, ale dwa nagrobki po przenoszeniu mogą wskazywać ten sam kalendarz.
        var calendarAreas = allAreas
            .Where(o => !o.Deleted && o.CalendarId is not null)
            .GroupBy(o => o.CalendarId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Do których kalendarzy umiemy i **wolno nam** pisać. Bez pierwszego okno
        // pokazywałoby pole wyboru przy wydarzeniu z kanału iCal, czyli przycisk bez
        // żadnego skutku. Bez drugiego pokazuje je przy wydarzeniu z kalendarza
        // świątecznego albo faz księżyca — czyli przycisk, który kończy się odmową
        // Google, a przy przenoszeniu kopią założoną, zanim odmowa przyszła.
        var writable = sources
            .Where(z => !z.ReadOnly && writers.Any(w => w.Kind == z.Kind))
            .Select(z => z.Id)
            .ToHashSet();

        // Zadania wczytane **przed** wydarzeniami, bo to one rozstrzygają, czego nie
        // rysować. Zadanie udostępnione ma w kalendarzu swoje odbicie, które wraca do
        // nas przy odświeżaniu — narysowane obok zadania dałoby dwa bloki na tę samą
        // rzecz, w tym samym miejscu, z których jeden nie dawałby się odhaczyć.
        // Prawdą jest zadanie; wydarzenie jest jego cieniem.
        var upcoming = await tasks.UpcomingAsync(from.AddDays(-1), from.AddDays(days), ct);

        // Wybory na dzień dołożone do umówionych. Zadanie wzięte „na dziś" w widoku
        // „Teraz" nie dostaje dnia wykonania — dostaje obietnicę daną sobie — więc
        // w kalendarzu nie było go widać wcale. A kalendarz jest jedynym miejscem,
        // w którym widać cały dzień naraz, i obietnica należy do niego tak samo jak
        // spotkanie. Ląduje na pasku całodniowym, bo godziny nie ma i zgadywanie jej
        // zrobiłoby z listy zadań kalendarz, w którym wszystko jest umówione.
        var selected = await tasks.FocusedBetweenAsync(from, from.AddDays(days), ct);

        var planned = upcoming
            .Concat(selected.Where(w => upcoming.All(u => u.Id != w.Id)))
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
        var mirrors = (await tasks.MirroredEventIdsAsync(ct)).ToHashSet(StringComparer.Ordinal);

        foreach (var ev in await store.EventsAsync(start, end, ct))
        {
            if (mirrors.Contains(ev.ExternalId))
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
            entries.Add(new AgendaEntry(
                EventMark.Strip(ev.Title),
                ev.IsAllDay
                    ? ev.StartsAt
                    : TimeZoneInfo.ConvertTime(ev.StartsAt, zone),
                ev.IsAllDay
                    ? ev.EndsAt
                    : TimeZoneInfo.ConvertTime(ev.EndsAt, zone),
                ev.IsAllDay,
                AgendaKind.Event,
                EventColor(ev.SourceId, calendarAreas, colors),
                TaskId: null,
                ev.SourceId,
                ev.ExternalId,
                IsDone: EventMark.IsDone(ev.Title),
                CanWrite: writable.Contains(ev.SourceId)));
        }

        // Barwy dziedziczone w dół: zadanie bierze swoją, a gdy jej nie ma — projektu,
        // a gdy i tego nie ma — obszaru. Ustawienie koloru raz na obszarze koloruje
        // więc wszystko, co do niego należy, bez dotykania pojedynczych zadań.
        var areaColors = allAreas.ToDictionary(o => o.Id, o => o.Color);
        var projectColors = ProjectColors(await projects.AllAsync(ct), areaColors);

        foreach (var task in planned)
        {
            if (Entry(task, zone, Color(task, projectColors, areaColors)) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return Agenda.Build(entries, from, days);
    }

    /// <summary>
    /// Zadanie na siatce. Bez godziny ląduje na pasku całodniowym — zgadywanie godziny
    /// zrobiłoby z listy zadań kalendarz, w którym wszystko jest umówione, a to jest
    /// dokładnie ten rodzaj planowania, który się nie utrzymuje.
    /// </summary>
    /// <summary>Chwila lokalna w strefie, z przesunięciem obowiązującym **tego dnia**.</summary>
    private static DateTimeOffset InZone(DateTime local, TimeZoneInfo zone) =>
        new(local, zone.GetUtcOffset(local));

    /// <summary>
    /// Barwa każdego projektu po rozwinięciu dziedziczenia: własna, rodzica, obszaru.
    /// </summary>
    /// <remarks>
    /// Rozwinięcie tutaj, a nie przy każdym zadaniu, bo podprojekt potrafi mieć kilku
    /// przodków, a zadań w tygodniu są setki. Przejście zabezpieczone licznikiem:
    /// po scaleniu dwóch urządzeń projekt umie stać się własnym przodkiem.
    /// </remarks>
    private static Dictionary<Guid, string?> ProjectColors(
        IReadOnlyList<Project> projects,
        IReadOnlyDictionary<Guid, string?> areas)
    {
        var byId = projects.ToDictionary(p => p.Id);
        var result = new Dictionary<Guid, string?>(projects.Count);

        foreach (var project in projects)
        {
            string? found = null;
            Project? current = project;

            for (var step = 0; step < projects.Count && current is not null; step++)
            {
                if (!string.IsNullOrWhiteSpace(current.Color))
                {
                    found = current.Color;
                    break;
                }

                current = current.ParentProjectId is { } parent
                    && byId.TryGetValue(parent, out var above)
                        ? above
                        : null;
            }

            result[project.Id] = found
                ?? (areas.TryGetValue(project.AreaId, out var fromArea) ? fromArea : null);
        }

        return result;
    }

    /// <summary>Barwa zadania: własna, projektu albo obszaru — w tej kolejności.</summary>
    private static string? Color(
        TaskItem task,
        IReadOnlyDictionary<Guid, string?> projects,
        IReadOnlyDictionary<Guid, string?> areas)
    {
        if (!string.IsNullOrWhiteSpace(task.Color))
        {
            return task.Color;
        }

        if (task.ProjectId is { } project
            && projects.TryGetValue(project, out var fromProject)
            && !string.IsNullOrWhiteSpace(fromProject))
        {
            return fromProject;
        }

        return task.AreaId is { } area && areas.TryGetValue(area, out var fromArea)
            ? fromArea
            : null;
    }

    private static AgendaEntry? Entry(TaskItem task, TimeZoneInfo zone, string? color)
    {
        // Dzień wykonania, a gdy go nie ma — dzień, na który zadanie zostało wybrane.
        // Kolejność nie jest dowolna: zadanie umówione na czwartek i wzięte na dziś
        // ma stać w czwartek, bo tam jest umówione. Wybór mówi „zajmę się tym", a nie
        // „to się wtedy odbywa".
        if ((task.DoDate ?? task.FocusDate) is not { } day)
        {
            return null;
        }

        if (task.DoDate is null || task.DoTime is not { } hour)
        {
            var dayStart = InZone(day.ToDateTime(TimeOnly.MinValue), zone);
            return new AgendaEntry(
                task.Title, dayStart, dayStart.AddDays(1),
                IsAllDay: true, AgendaKind.Task, color, task.Id,
                SourceId: null, ExternalId: null, IsDone: task.State == TaskState.Done,
                CanWrite: true);
        }

        var start = InZone(day.ToDateTime(hour), zone);
        var length = TimeSpan.FromMinutes(task.EstimatedMinutes ?? 30);

        return new AgendaEntry(
            task.Title, start, start + length, IsAllDay: false, AgendaKind.Task,
            color, task.Id, SourceId: null, ExternalId: null,
            IsDone: task.State == TaskState.Done, CanWrite: true);
    }
}
