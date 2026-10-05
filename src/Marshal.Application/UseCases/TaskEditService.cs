using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.Repositories;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>Co da się zmienić w istniejącym zadaniu.</summary>
public sealed record TaskEdit(
    string Title,
    string? Note,
    DateOnly? DoDate,
    DateOnly? Deadline,
    DateTimeOffset? ReminderAt,
    RecurrenceRule? Recurrence,
    Priority Priority,
    int? EstimatedMinutes = null,
    Energy Energy = Energy.Unknown,

    /// <summary>Obszar. Puste znaczy „zostaw ten, który jest".</summary>
    Guid? AreaId = null,

    /// <summary>Godzina rozpoczęcia. Bez niej zadanie idzie na pasek całodniowy.</summary>
    TimeOnly? DoTime = null,

    /// <summary>
    /// Wyprzedzenia liczone od godziny zadania, w minutach. Puste znaczy „zostaw te,
    /// które są" — pusta lista znaczy „żadnych", i to są dwie różne rzeczy.
    /// </summary>
    IReadOnlyList<int>? ReminderLeads = null);

/// <summary>
/// Zmiana pól zadania z jednego miejsca (spec 11, ekran szczegółu).
/// </summary>
/// <remarks>
/// Okno nie dotyka zegara logicznego samo. Znacznik wydawany jest tutaj, przy każdej
/// zmienionej rzeczy z osobna, bo scalanie działa per pole (9.4) i zmiana samego terminu
/// nie ma unieważniać tytułu poprawionego na drugim urządzeniu.
/// </remarks>
public sealed class TaskEditService(
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IHlcSource hlc,
    IClock clock,
    IAreaRepository areas,
    ITaskMirror mirror,
    ITaskSeriesRepository series,
    SeriesService rhythms)
{
    /// <summary>
    /// Wyrównanie odbicia w kalendarzu po zapisie.
    /// </summary>
    /// <remarks>
    /// Wołane z każdej ścieżki, która zmienia to, co widać w wydarzeniu: nazwę, dzień,
    /// godzinę, długość i odhaczenie. O tym, czy jest co wysyłać, rozstrzyga samo
    /// odbicie: zadanie bez dnia albo bez godziny odpada, a bez ustawionego kalendarza
    /// głównego odpada wszystko. Sprawdzanie tego tutaj znaczyłoby dwa miejsca z tą
    /// samą regułą — i to właśnie przez takie sprawdzenie zadania nie trafiały do
    /// kalendarza głównego same.
    ///
    /// Po zapisie u nas, nie przed: baza jest prawdą, a kalendarz jej odbiciem. Gdyby
    /// wysyłka szła pierwsza, nieudany zapis lokalny zostawiałby w cudzym kalendarzu
    /// wydarzenie opisujące zadanie, które u nas wygląda inaczej.
    /// </remarks>
    private async Task MirrorAsync(TaskItem? task, CancellationToken ct)
    {
        if (task is not null)
        {
            await mirror.PushAsync(task, ct);
        }
    }

    public async Task<TaskItem?> ApplyAsync(Guid id, TaskEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        // Każde pole ruszane tylko wtedy, gdy naprawdę się zmieniło. Zapis „na wszelki
        // wypadek" trafiłby do dziennika jako świeża decyzja i wygrał scalanie
        // ze zmianą, której użytkownik naprawdę dokonał gdzie indziej.
        if (!string.IsNullOrWhiteSpace(edit.Title) && edit.Title.Trim() != task.Title)
        {
            task.Rename(edit.Title, hlc.Next());
        }

        if ((edit.Note ?? string.Empty) != (task.Note ?? string.Empty))
        {
            task.SetNote(edit.Note, hlc.Next());
        }

        if (edit.Deadline != task.Deadline)
        {
            task.SetDeadline(edit.Deadline, hlc.Next());
        }

        if (edit.ReminderAt != task.ReminderAt)
        {
            task.SetReminder(edit.ReminderAt, hlc.Next());
        }

        // Porównanie po uporządkowaniu, bo zadanie trzyma je bez powtórzeń i rosnąco.
        // Inaczej ta sama lista w innej kolejności wyglądałaby na zmianę i wygrywała
        // scalanie z prawdziwą zmianą z drugiego urządzenia.
        if (edit.ReminderLeads is { } leads
            && !leads.Where(m => m >= 0).Distinct().Order().SequenceEqual(task.ReminderLeads))
        {
            task.SetReminderLeads(leads, hlc.Next());
        }

        if (edit.Priority != task.Priority)
        {
            task.SetPriority(edit.Priority, hlc.Next());
        }

        if (edit.EstimatedMinutes != task.EstimatedMinutes || edit.Energy != task.Energy)
        {
            task.SetEstimate(edit.EstimatedMinutes, edit.Energy, hlc.Next());
        }

        // Zmiana obszaru rusza stan tylko wtedy, gdy zadanie już wyszło ze skrzynki:
        // przetwarzanie jest osobnym krokiem i zapisanie szczegółu nie ma go zastępować.
        var areaChanged = edit.AreaId is { } created
            && created != task.AreaId
            && task.State != TaskState.Inbox;

        if (edit.DoDate != task.DoDate || areaChanged)
        {
            await ApplyDoDateAsync(task, edit.DoDate, edit.AreaId, ct);
        }

        // Godzina po dniu, bo bez dnia nie ma czego trzymać — i po przejściu stanu,
        // bo MakeNext ją czyści.
        if (edit.DoTime != task.DoTime)
        {
            task.SetDoTime(edit.DoTime, hlc.Next());
        }

        // Rytm **na końcu**, po dniu i porze. Seria bierze z zadania początek i szablon,
        // więc ustawiona wcześniej dostawała dzień i godzinę sprzed tego zapisu —
        // wpisanie daty i rytmu naraz zakładało serię od starego dnia.
        //
        // Reguła porównywana z regułą **serii**, nie z regułą zadania. Wystąpienie serii
        // nie nosi już reguły, więc porównanie z nim samym widziałoby zmianę przy każdym
        // zapisie i przy każdym zapisie nazwy stawiało okno serii od nowa.
        var standing = task.SeriesId is { } mine ? await series.FindAsync(mine, ct) : null;

        // **Dokładne pytanie, nie przybliżone.** Zapis karty niesie regułę zawsze, bo
        // pora i długość są w niej zapamiętywane — więc samo porównanie reguł mówiło
        // „zmieniło się" przy każdym zapisie, a każde takie „zmieniło się" wczytywało
        // wszystkie wystąpienia serii i przechodziło je po kolei. Stąd zapis, który
        // trwał tak długo, że wyglądał na niedziałający.
        //
        // Pytane jest więc o jedno i drugie: reguła i szablon. Gdy oba są te same,
        // seria nie jest w ogóle dotykana i zapis kosztuje jeden wiersz.
        var ruleChanged = standing is { } current
            ? edit.Recurrence is null
                || edit.Recurrence != current.Rule
                || SeriesService.Blend(SeriesTemplate.From(task), edit.Recurrence)
                    != current.Template
            : edit.Recurrence != task.Recurrence;

        if (ruleChanged)
        {
            // Dzień i pora już zapisane, żeby szablon serii wyszedł z tego, co zapisane,
            // a nie z tego, co było przed chwilą.
            await unitOfWork.SaveChangesAsync(ct);

            switch (edit.Recurrence, standing)
            {
                case ({ } fresh, null):
                    await rhythms.StartAsync(task, fresh, ct);
                    break;

                case ({ } fresh, { } one):
                    await rhythms.ChangeAsync(one, SeriesTemplate.From(task), fresh, ct);
                    break;

                // Pusta sekcja rytmu przy serii znaczy „to ostatnie wystąpienie" —
                // tak samo jak „nie powtarza się" z menu. Reguła leży w serii, więc
                // nie ma już czego z zadania zdejmować; zostaje pytanie o dalsze dni
                // i jedna sensowna odpowiedź na nie.
                case (null, { } one):
                    await rhythms.EndAsync(one, task.DoDate ?? clock.Today, ct);
                    break;

                case (null, null):
                    task.SetRecurrence(null, hlc.Next());
                    break;
            }
        }

        // Zapisanie karty wystąpienia znaczy, że ten jeden dzień został zmieniony
        // świadomie — i od tej chwili zmiana szablonu serii go nie dotyka. Bez tego
        // znacznika poprawiona godzina jednego wtorku przepadałaby przy najbliższej
        // zmianie nazwy całej serii, bez pytania i bez śladu.
        //
        // **Także wtedy, gdy zapis zmieniał rytm.** Karta pokazuje jedno wystąpienie
        // i to na nie się patrzy, naciskając „Zapisz"; nowy rytm idzie przy tym do serii
        // razem z szablonem zdjętym z tej właśnie karty, więc dzień, na który się
        // patrzyło, zostaje taki, jak go zapisano.
        if (task.SeriesId is not null)
        {
            task.Override(hlc.Next());
        }

        await unitOfWork.SaveChangesAsync(ct);
        await MirrorAsync(task, ct);

        return task;
    }

    /// <summary>
    /// Dopisanie długości i poziomu sił — bez ruszania reszty.
    /// </summary>
    /// <remarks>
    /// Osobno od pełnej edycji, bo przetwarzanie skrzynki dopisuje właśnie te dwie
    /// rzeczy i nic więcej. Przepuszczenie tego przez edycję wszystkich pól wysyłałoby
    /// do scalania także tytuł i termin, których nikt nie dotykał (9.4).
    /// </remarks>
    public async Task<TaskItem?> SetEstimateAsync(
        Guid id, int? minutes, Energy energy, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        if (task.EstimatedMinutes != minutes || task.Energy != energy)
        {
            task.SetEstimate(minutes, energy, hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);
        }

        return task;
    }

    /// <summary>
    /// Sama długość, bez ruszania poziomu sił.
    /// </summary>
    /// <remarks>
    /// Rozciągnięcie bloku na siatce zmienia dokładnie jedną rzecz. Przepuszczenie tego
    /// przez <see cref="SetEstimateAsync"/> wysyłałoby do scalania także siłę, której
    /// nikt nie dotykał — a scalanie działa per pole (9.4).
    /// </remarks>
    public async Task<TaskItem?> SetMinutesAsync(
        Guid id, int minutes, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        if (task.EstimatedMinutes != minutes)
        {
            // Seria zapamiętuje swoją długość, zanim to jedno wystąpienie zrobi się
            // inne. Zadanie niosące rytm jest jednocześnie wystąpieniem i wzorcem serii,
            // więc bez tego kroku rozciągnięcie dzisiejszego bloku przerysowywałoby
            // wszystkie zapowiedzi — długość brały właśnie stąd.
            if (task.Recurrence is { Minutes: null } rhythm)
            {
                task.SetRecurrence(
                    rhythm.WithLength(task.EstimatedMinutes ?? DefaultLength), hlc.Next());
            }

            task.SetEstimate(minutes, task.Energy, hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);
            await MirrorAsync(task, ct);
        }

        return task;
    }

    /// <summary>
    /// Domyślna długość bloku na siatce. Ta sama, którą rysuje kalendarz przy zadaniu
    /// bez oszacowania — inaczej zapamiętana długość serii przeskakiwałaby przy pierwszym
    /// rozciągnięciu na wartość, której nigdzie nie było widać.
    /// </summary>
    private const int DefaultLength = 30;

    /// <summary>Waga — jedno pole, jedna zmiana (menu podręczne).</summary>
    public Task<TaskItem?> SetPriorityAsync(
        Guid id, Priority priority, CancellationToken ct = default) =>
        ChangeAsync(id, z => z.SetPriority(priority, hlc.Next()), ct);

    /// <summary>Rytm z menu: same rodzaje, bez zaczepienia i pominięć — te mają swój ekran.</summary>
    /// <summary>
    /// Nadanie rytmu, zmiana rodzaju albo koniec serii — jedną drogą.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pusty rodzaj to „nie powtarza się" i przy wystąpieniu serii znaczy <b>koniec serii
    /// na tym dniu</b>, a nie zdjęcie pola. Reguła leży w serii, więc nie ma już czego
    /// z zadania zdejmować; pytanie „co z dalszymi wystąpieniami" trzeba za to postawić
    /// i jest na nie jedna sensowna odpowiedź — to, na co się patrzy, zostaje, dalszych
    /// nie ma.
    /// </para>
    /// <para>
    /// Zmiana rodzaju przy serii już istniejącej idzie do serii, nie zakłada drugiej.
    /// Dwie serie o tym samym szablonie znaczyłyby dwa wystąpienia na dzień — czyli
    /// dokładnie to, przed czym cała ta przebudowa ma chronić.
    /// </para>
    /// </remarks>
    public async Task<TaskItem?> SetRecurrenceAsync(
        Guid id, RecurrenceKind? kind, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        var standing = task.SeriesId is { } mine ? await series.FindAsync(mine, ct) : null;

        switch (kind, standing)
        {
            case (null, { } one):
                await rhythms.EndAsync(one, task.DoDate ?? clock.Today, ct);
                break;

            case (null, null):
                task.SetRecurrence(null, hlc.Next());
                await unitOfWork.SaveChangesAsync(ct);
                break;

            case ({ } value, null):
                await rhythms.StartAsync(task, new RecurrenceRule(value), ct);
                break;

            case ({ } value, { } one):
                await rhythms.ChangeAsync(
                    one, SeriesTemplate.From(task), new RecurrenceRule(value), ct);
                break;
        }

        return task;
    }

    /// <summary>
    /// Reguła, którą zadanie się rządzi — z serii, a przy zadaniu bez serii z niego samego.
    /// </summary>
    /// <remarks>
    /// Okno szczegółu pokazuje sekcję rytmu i musi ją czymś wypełnić. Czytane z samego
    /// zadania pokazywałoby „nie powtarza się" przy każdym wystąpieniu serii — czyli
    /// kłamałoby o rzeczy, którą ten ekran służy zmieniać.
    /// </remarks>
    public async Task<RecurrenceRule?> RuleOfAsync(TaskItem task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.SeriesId is not { } mine)
        {
            return task.Recurrence;
        }

        return (await series.FindAsync(mine, ct))?.Rule ?? task.Recurrence;
    }

    /// <summary>
    /// Koniec całej serii: razem z tym wystąpieniem i z tym, co stało dalej.
    /// </summary>
    /// <remarks>
    /// Inny wynik niż „to ostatnie wystąpienie" i dlatego osobna droga: tamto jest dla
    /// serii, która się skończyła, ta dla serii, która była pomyłką. Wystąpienia minione
    /// zostają — zdarzyły się i historia o tym wie.
    /// </remarks>
    public async Task<bool> DropSeriesAsync(Guid id, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { SeriesId: { } mine } task
            || await series.FindAsync(mine, ct) is not { } one)
        {
            return false;
        }

        await rhythms.DropAsync(one, ct);

        if (task.State is not TaskState.Trashed)
        {
            task.Trash(hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <summary>Przeniesienie do projektu albo wyjęcie z niego.</summary>
    public async Task<TaskItem?> SetProjectAsync(
        Guid id, Guid? projectId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        var area = task.AreaId ?? (await areas.ActiveAsync(ct)).FirstOrDefault()?.Id;

        if (area is not { } areaId)
        {
            throw new InvalidOperationException(
                "Nie ma żadnego czynnego obszaru, a zadanie w projekcie musi do któregoś należeć.");
        }

        task.MoveTo(areaId, projectId, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return task;
    }

    private async Task<TaskItem?> ChangeAsync(
        Guid id, Action<TaskItem> change, CancellationToken ct)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        change(task);
        await unitOfWork.SaveChangesAsync(ct);

        return task;
    }

    /// <summary>
    /// Przełożenie zadania na inny dzień i godzinę — jednym ruchem, bez reszty pól.
    /// </summary>
    /// <remarks>
    /// Osobno od <see cref="ApplyAsync"/>, bo przeciągnięcie po siatce zmienia dokładnie
    /// dwie rzeczy. Przepuszczenie tego przez pełną edycję znaczyłoby wysłanie do
    /// scalania wszystkich pól naraz — a wtedy przeciągnięcie bloku na komputerze
    /// unieważniałoby tytuł poprawiony w tej samej minucie na telefonie (9.4).
    /// </remarks>
    public async Task<TaskItem?> RescheduleAsync(
        Guid id, DateOnly day, TimeOnly? time, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        // Seria zapamiętuje swoją porę, zanim to jedno wystąpienie zacznie się kiedy
        // indziej. Bez tego kroku przeciągnięcie dzisiejszego bloku o godzinę w dół
        // przestawiało wszystkie zapowiedzi — porę brały właśnie stąd.
        if (task.Recurrence is { Time: null } rhythm && task.DoTime is { } was && time != was)
        {
            task.SetRecurrence(rhythm.WithHour(was), hlc.Next());
        }

        await ApplyDoDateAsync(task, day, task.AreaId, ct);
        task.SetDoTime(time, hlc.Next());

        // Przestawione palcem po siatce zostaje przestawione: zob. zapis karty wyżej.
        if (task.SeriesId is not null)
        {
            task.Override(hlc.Next());
        }

        await unitOfWork.SaveChangesAsync(ct);
        await MirrorAsync(task, ct);

        return task;
    }

    /// <summary>
    /// Odhaczenie zadania — razem z kolejnym wystąpieniem, jeśli się powtarza (8.4).
    /// </summary>
    public async Task<TaskItem?> CompleteAsync(Guid id, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        // Odhaczenie **nie dotyka godziny ani dnia**. Zadanie bez godziny zostaje bez
        // godziny, także wtedy, gdy jest zrobione.
        //
        // Dotąd dopisywało blok kończący się „teraz" — żeby wieczorem było z czego
        // odczytać, na co poszedł dzień. Cel był dobry, a droga zła: godzina została
        // wpisana w pole, które znaczy **decyzję**, a nie zapis tego, co się stało.
        // Dwie linijki niżej stało o tym wprost: „godziny wpisanej wcześniej nie
        // ruszamy — to była decyzja, a nie zapis tego, co się stało". Ta sama zasada
        // zabrania wpisywać tam godzinę, której nikt nie wybrał.
        //
        // Ta godzina nie była nawet zapisem prawdy: brana z zegara zaokrąglonego do
        // pięciu minut i cofnięta o **oszacowanie**, czyli o liczbę, którą ktoś kiedyś
        // zgadł. Rzecz wstawiona w dzień jako punkt bez pory dostawała po odhaczeniu
        // „05:00" i przestawała być punktem — w widoku miesiąca widać to od razu, bo
        // tam wpis bez pory jest właśnie znacznikiem dnia.
        //
        // Chwila wykonania nie ginie: niesie ją TaskItem.CompletedAt, czyli pole, które
        // znaczy dokładnie to. Przy okazji odhaczenie przestało przestawiać dzień
        // wykonania na dzisiaj — rzecz zrobiona w czwartek ma zostać w czwartek.
        task.Complete(clock.Now, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        // Odhaczenie **nie rodzi już następnika**: wystąpienia stoją w bazie z góry,
        // a okno dopełnia się o jedno na drugim końcu. Rodzenie następnika było tym
        // miejscem, w którym tożsamość serii brała się z poprzednika i z dnia odhaczenia
        // — czyli z chwili, w której ktoś akurat kliknął. Dwa urządzenia klikające
        // w różnych dniach rozchodziły się tu na dwa łańcuchy.
        var next = await AfterCompletionAsync(task, ct);

        await MirrorAsync(task, ct);

        return next;
    }

    /// <summary>
    /// Co po odhaczeniu wystąpienia serii.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Przy rytmie zaczepionym na kalendarzu — nic: następne wystąpienia stoją już
    /// w bazie, a okno dopełni się przy najbliższym przejściu dnia.
    /// </para>
    /// <para>
    /// Przy rytmie zaczepionym na <b>wykonaniu</b> — przestawienie początku serii na
    /// dzisiaj i dopełnienie. „Co 3 dni od wykonania" nie ma dat do wyliczenia, dopóki
    /// poprzednie nie zostanie odhaczone, więc okno takiej serii sięga na jedno
    /// wystąpienie i liczy się od ostatniego wykonania. Dzień wykonania jest przy tym
    /// zapisem, który obchodzi oba urządzenia — nie chwilą, w której jedno z nich było
    /// akurat otwarte — więc oba dojdą do tej samej daty i do tego samego wiersza.
    /// </para>
    /// </remarks>
    private async Task<TaskItem?> AfterCompletionAsync(TaskItem task, CancellationToken ct)
    {
        if (task.SeriesId is not { } mine || await series.FindAsync(mine, ct) is not { } one)
        {
            return null;
        }

        if (one.Rule.Anchor == RecurrenceAnchor.FromCompletion)
        {
            one.MoveStart(clock.Today, hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);
        }

        await rhythms.TopUpAsync(one, ct);

        return (await series.OccurrencesAsync(mine, ct))
            .Where(z => z.DoDate >= clock.Today
                     && z.Id != task.Id
                     && z.State is TaskState.Scheduled or TaskState.Next or TaskState.Waiting)
            .OrderBy(z => z.DoDate)
            .FirstOrDefault();
    }

    /// <summary>
    /// Pominięcie bieżącego wystąpienia rytmu: to jedno przepada, rytm idzie dalej.
    /// </summary>
    /// <remarks>
    /// Wyrzucenie zadania z rytmem kasowało dotąd całą serię, bo regułę niesie właśnie
    /// to wystąpienie — jedna czynność na dwie różne rzeczy, z czego drugiej nikt nie
    /// chciał. Tędy przepada wyłącznie ten jeden raz.
    /// </remarks>
    public async Task<TaskItem?> SkipOccurrenceAsync(Guid id, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        // Nagrobek na tym jednym wierszu i nic więcej. W starym modelu trzeba tu było
        // zrodzić następnika, bo wyrzucane wystąpienie niosło regułę i razem z nim
        // przepadałby cały rytm; teraz reguła leży w serii, a dalsze dni stoją już
        // w bazie. Nagrobek z wyliczoną tożsamością jest przy tym zapisem trwałym:
        // dopełnianie okna pomija ten dzień, więc „tej środy nie będzie" nie wraca
        // przy najbliższym uruchomieniu.
        var next = task.SeriesId is null
            ? RecurrenceRunner.Skip(task, clock.Now, hlc.Next)
            : null;

        if (next is not null)
        {
            tasks.Add(next);
        }
        else
        {
            task.Trash(hlc.Next());
        }

        await unitOfWork.SaveChangesAsync(ct);

        // Odbicie w kalendarzu idzie za zadaniem: wyrzuconego wystąpienia nie ma już
        // po co pokazywać osobie, której było udostępnione.
        await MirrorAsync(task, ct);

        return next;
    }

    /// <summary>
    /// Odwołanie jednego z wystąpień narysowanych do przodu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zmiana zapisuje się w regule, przy dniu, w którym wystąpienie wypadało. Rytm
    /// biegnie dalej po swojemu: odwołana środa nie przesuwa kolejnej ani nie dokłada
    /// niczego na koniec serii liczonej na wystąpienia.
    /// </para>
    /// <para>
    /// Dotyczy wyłącznie wystąpień, których jeszcze nie ma. To niosące regułę jest
    /// zwykłym zadaniem — od jego pominięcia jest <see cref="SkipOccurrenceAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="id">Zadanie niosące rytm.</param>
    /// <param name="occurrence">Dzień, w którym wystąpienie wypada z reguły.</param>
    public async Task<TaskItem?> DropOccurrenceAsync(
        Guid id, DateOnly occurrence, CancellationToken ct = default) =>
        await ChangeOccurrenceAsync(
            id, occurrence, _ => new RecurrenceChange(occurrence, Dropped: true), ct);

    /// <summary>
    /// Przełożenie jednego z wystąpień narysowanych do przodu na inny dzień albo porę.
    /// </summary>
    /// <remarks>
    /// Pora zapisana przy zmianie dotyczy tego jednego razu. Rytm zostaje ze swoją —
    /// „w tę środę wyjątkowo o siedemnastej" nie znaczy „od teraz o siedemnastej",
    /// a gdyby znaczyło, nie dałoby się powiedzieć tego pierwszego.
    /// </remarks>
    public async Task<TaskItem?> MoveOccurrenceAsync(
        Guid id, DateOnly occurrence, DateOnly day, TimeOnly? time, CancellationToken ct = default) =>
        await ChangeOccurrenceAsync(
            id,
            occurrence,
            before => new RecurrenceChange(
                occurrence, Day: day, Time: time, Minutes: before?.Minutes),
            ct);

    /// <summary>
    /// Zmiana długości jednego z wystąpień narysowanych do przodu.
    /// </summary>
    /// <remarks>
    /// Rozciągnięcie dolnej krawędzi zapowiedzi. Rytm zostaje ze swoją długością —
    /// „w tę środę wyjątkowo dłużej" nie znaczy „od teraz to trwa dłużej", a gdyby
    /// znaczyło, nie dałoby się powiedzieć tego pierwszego.
    /// </remarks>
    public async Task<TaskItem?> ResizeOccurrenceAsync(
        Guid id, DateOnly occurrence, int minutes, CancellationToken ct = default) =>
        await ChangeOccurrenceAsync(
            id,
            occurrence,
            before => new RecurrenceChange(
                occurrence,
                Dropped: before?.Dropped ?? false,
                Day: before?.Day,
                Time: before?.Time,
                Minutes: minutes),
            ct);

    /// <param name="patch">
    /// Nowa zmiana złożona z tej, która już przy tym dniu stała. Wpis jest jeden na dzień,
    /// więc przełożenie i długość muszą umieć dotyczyć tego samego wystąpienia — inaczej
    /// rozciągnięcie przełożonej środy cofałoby ją tam, skąd się ją zabrało.
    /// </param>
    /// <summary>
    /// Wyjęcie jednego z wystąpień narysowanych do przodu na osobne zadanie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zapowiedź nie jest zadaniem, więc nie ma przypomnienia, nie da się jej pokazać
    /// osobie ani dopisać do niej notatki. Tędy staje się zwykłym zadaniem na swój dzień,
    /// z porą i długością, które miała na siatce — a seria przestaje je produkować.
    /// </para>
    /// <para>
    /// Odwołanie w regule, a nie osobny rodzaj wpisu: „ta seria tego dnia nie produkuje"
    /// znaczy dokładnie to, co trzeba, i jest już w modelu. Wystąpienie zużywa przy tym
    /// swój numer w serii liczonej na wystąpienia — bo się odbędzie, tyle że osobno.
    /// </para>
    /// </remarks>
    /// <returns>Nowe zadanie albo <c>null</c>, gdy nie było czego wyjąć.</returns>
    public async Task<TaskItem?> DetachOccurrenceAsync(
        Guid id, DateOnly occurrence, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } rhythm)
        {
            return null;
        }

        if (RecurrenceRunner.Detach(rhythm, occurrence, clock.Now, hlc.Next) is not { } alone)
        {
            return null;
        }

        tasks.Add(alone);

        await unitOfWork.SaveChangesAsync(ct);
        await MirrorAsync(alone, ct);

        return alone;
    }

    /// <summary>
    /// Dzień, pora i długość jednego z wystąpień narysowanych do przodu — naraz.
    /// </summary>
    /// <remarks>
    /// Droga z karty wystąpienia, gdzie wszystkie trzy rzeczy zmienia się jednym zapisem.
    /// Osobno od przełożenia i rozciągnięcia, bo tamte przychodzą z siatki i każde mówi
    /// o czymś jednym — a zapis karty jest jedną decyzją o całym wystąpieniu.
    /// </remarks>
    public async Task<TaskItem?> SetOccurrenceAsync(
        Guid id,
        DateOnly occurrence,
        DateOnly day,
        TimeOnly? time,
        int? minutes,
        CancellationToken ct = default) =>
        await ChangeOccurrenceAsync(
            id,
            occurrence,
            _ => new RecurrenceChange(occurrence, Day: day, Time: time, Minutes: minutes),
            ct);

    private async Task<TaskItem?> ChangeOccurrenceAsync(
        Guid id,
        DateOnly occurrence,
        Func<RecurrenceChange?, RecurrenceChange> patch,
        CancellationToken ct)
    {
        if (await tasks.FindAsync(id, ct) is not { Recurrence: { } rule } task)
        {
            return null;
        }

        // Zmiana wystąpienia minionego albo tego, które regułę niesie, nie ma czego
        // dotyczyć: tamte są już zadaniami albo nie powstaną nigdy.
        if (task.DoDate is { } current && occurrence <= current)
        {
            return null;
        }

        task.SetRecurrence(rule.With(patch(rule.ChangeOn(occurrence))), hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return task;
    }

    /// <summary>
    /// Zdjęcie ptaszka — zadanie znowu jest do zrobienia.
    /// </summary>
    /// <remarks>
    /// Odhaczenie dało się dotąd cofnąć wyłącznie przez bazę: żaden ekran nie miał
    /// takiej czynności, mimo że model ją umiał. Odhaczenie jest decyzją podejmowaną
    /// jednym kliknięciem, więc omyłkowe zdarza się tak samo łatwo — i musi kosztować
    /// tyle samo.
    /// </remarks>
    public async Task<TaskItem?> ReopenAsync(Guid id, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } task)
        {
            return null;
        }

        if (task.State != TaskState.Done)
        {
            return task;
        }

        task.Reopen(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
        await MirrorAsync(task, ct);

        return task;
    }

    /// <summary>
    /// Nadanie i zdjęcie dnia wykonania.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Przechodzi przez przejścia stanu, nie przez samo pole: N8 wymaga, żeby zadanie
    /// <c>Scheduled</c> miało datę, a zadanie bez daty nie było <c>Scheduled</c>.
    /// </para>
    /// <para>
    /// <b>Zadanie bez obszaru dostaje obszar.</b> Do dziś oba przejścia wymagały, żeby
    /// obszar już był — a zadanie z wrzutu go nie ma. Data ustawiona na ekranie
    /// szczegółu **znikała bez słowa**: zapis się udawał, okno się zamykało, a zadanie
    /// nie pojawiało się ani w „Dzisiaj", ani w „Planach", ani na siatce kalendarza.
    /// Wybór obszaru jest decyzją użytkownika (i jest w szczegółach), ale gdy go nie
    /// podano, pierwszy czynny obszar jest odpowiedzią lepszą niż cisza.
    /// </para>
    /// </remarks>
    private async Task ApplyDoDateAsync(
        TaskItem task, DateOnly? doDate, Guid? selected, CancellationToken ct)
    {
        // Zadanie odhaczone dostaje sam dzień, bez przejścia stanu. Przejście ustawia
        // „zaplanowane" i tym samym zdejmuje „wykonane" — więc przeciągnięcie
        // wykonanego bloku po siatce wskrzeszało go, a zapis szczegółu odhaczonego
        // zadania cofał odhaczenie. Ruch po siatce poprawia zapis o przeszłości;
        // od cofnięcia decyzji jest zdjęcie ptaszka, osobną czynnością.
        if (task.State == TaskState.Done)
        {
            task.MoveDoDate(doDate, hlc.Next());
            return;
        }

        var area = selected ?? task.AreaId ?? (await areas.ActiveAsync(ct)).FirstOrDefault()?.Id;

        if (area is not { } id)
        {
            throw new InvalidOperationException(
                "Nie ma żadnego czynnego obszaru, a zadanie z dniem wykonania musi do "
                + "któregoś należeć. Załóż albo włącz obszar na ekranie „Obszary i projekty”.");
        }

        if (doDate is { } day)
        {
            task.Schedule(id, day, hlc.Next());
        }
        else
        {
            task.MakeNext(id, hlc.Next());
        }
    }
}
