using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.Repositories;
using Marshal.Domain.Recurrence;
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
    ITaskMirror mirror)
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

        if (edit.Recurrence != task.Recurrence)
        {
            task.SetRecurrence(edit.Recurrence, hlc.Next());
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
    public Task<TaskItem?> SetRecurrenceAsync(
        Guid id, RecurrenceKind? kind, CancellationToken ct = default) =>
        ChangeAsync(
            id,
            z => z.SetRecurrence(
                kind is { } value ? new RecurrenceRule(value) : null, hlc.Next()),
            ct);

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

        // Odhaczenie zostawia ślad na siatce: blok kończy się **teraz**, a zaczyna
        // tyle wcześniej, ile zadanie miało trwać. Zadanie bez godziny znikało dotąd
        // z kalendarza bez śladu, więc wieczorem nie było z czego odczytać, na co
        // poszedł dzień. Godziny wpisanej wcześniej nie ruszamy — to była decyzja,
        // a nie zapis tego, co się stało.
        if (task.DoTime is null && task.State != TaskState.Done)
        {
            await SaveDoTimeAsync(task, ct);
        }

        var next = RecurrenceRunner.Complete(task, clock.Now, hlc.Next);

        if (next is not null)
        {
            tasks.Add(next);
        }

        await unitOfWork.SaveChangesAsync(ct);
        await MirrorAsync(task, ct);

        return next;
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

        var next = RecurrenceRunner.Skip(task, clock.Now, hlc.Next);

        if (next is not null)
        {
            tasks.Add(next);
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

    /// <summary>Blok kończący się teraz, o długości równej oszacowaniu.</summary>
    private async Task SaveDoTimeAsync(TaskItem task, CancellationToken ct)
    {
        var now = clock.Now;

        // Do pięciu minut w dół: „skończone o 14:37" jest dokładniejsze, niż bywa prawda.
        var end = new TimeOnly(now.Hour, now.Minute / 5 * 5);
        var length = TimeSpan.FromMinutes(task.EstimatedMinutes ?? DefaultMinutes);

        // Początek przycięty do północy: blok ma opisać dzisiaj, a nie sięgnąć wstecz
        // na wczoraj przez zadanie oszacowane na trzy godziny i odhaczone o pierwszej.
        var start = end.ToTimeSpan() > length
            ? TimeOnly.FromTimeSpan(end.ToTimeSpan() - length)
            : TimeOnly.MinValue;

        await ApplyDoDateAsync(task, clock.Today, task.AreaId, ct);
        task.SetDoTime(start, hlc.Next());
    }

    /// <summary>Ile trwa zadanie bez oszacowania (spec 11).</summary>
    private const int DefaultMinutes = 30;

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
