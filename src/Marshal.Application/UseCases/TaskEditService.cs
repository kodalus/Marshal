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
    TimeOnly? DoTime = null);

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
    private async Task OdbijAsync(TaskItem? zadanie, CancellationToken ct)
    {
        if (zadanie is not null)
        {
            await mirror.PushAsync(zadanie, ct);
        }
    }

    public async Task<TaskItem?> ApplyAsync(Guid id, TaskEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (await tasks.FindAsync(id, ct) is not { } zadanie)
        {
            return null;
        }

        // Każde pole ruszane tylko wtedy, gdy naprawdę się zmieniło. Zapis „na wszelki
        // wypadek" trafiłby do dziennika jako świeża decyzja i wygrał scalanie
        // ze zmianą, której użytkownik naprawdę dokonał gdzie indziej.
        if (!string.IsNullOrWhiteSpace(edit.Title) && edit.Title.Trim() != zadanie.Title)
        {
            zadanie.Rename(edit.Title, hlc.Next());
        }

        if ((edit.Note ?? string.Empty) != (zadanie.Note ?? string.Empty))
        {
            zadanie.SetNote(edit.Note, hlc.Next());
        }

        if (edit.Deadline != zadanie.Deadline)
        {
            zadanie.SetDeadline(edit.Deadline, hlc.Next());
        }

        if (edit.ReminderAt != zadanie.ReminderAt)
        {
            zadanie.SetReminder(edit.ReminderAt, hlc.Next());
        }

        if (edit.Priority != zadanie.Priority)
        {
            zadanie.SetPriority(edit.Priority, hlc.Next());
        }

        if (edit.EstimatedMinutes != zadanie.EstimatedMinutes || edit.Energy != zadanie.Energy)
        {
            zadanie.SetEstimate(edit.EstimatedMinutes, edit.Energy, hlc.Next());
        }

        if (edit.Recurrence != zadanie.Recurrence)
        {
            zadanie.SetRecurrence(edit.Recurrence, hlc.Next());
        }

        // Zmiana obszaru rusza stan tylko wtedy, gdy zadanie już wyszło ze skrzynki:
        // przetwarzanie jest osobnym krokiem i zapisanie szczegółu nie ma go zastępować.
        var obszarSieZmienil = edit.AreaId is { } nowy
            && nowy != zadanie.AreaId
            && zadanie.State != TaskState.Inbox;

        if (edit.DoDate != zadanie.DoDate || obszarSieZmienil)
        {
            await ApplyDoDateAsync(zadanie, edit.DoDate, edit.AreaId, ct);
        }

        // Godzina po dniu, bo bez dnia nie ma czego trzymać — i po przejściu stanu,
        // bo MakeNext ją czyści.
        if (edit.DoTime != zadanie.DoTime)
        {
            zadanie.SetDoTime(edit.DoTime, hlc.Next());
        }

        await unitOfWork.SaveChangesAsync(ct);
        await OdbijAsync(zadanie, ct);

        return zadanie;
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
        if (await tasks.FindAsync(id, ct) is not { } zadanie)
        {
            return null;
        }

        if (zadanie.EstimatedMinutes != minutes || zadanie.Energy != energy)
        {
            zadanie.SetEstimate(minutes, energy, hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);
        }

        return zadanie;
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
        if (await tasks.FindAsync(id, ct) is not { } zadanie)
        {
            return null;
        }

        if (zadanie.EstimatedMinutes != minutes)
        {
            zadanie.SetEstimate(minutes, zadanie.Energy, hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);
            await OdbijAsync(zadanie, ct);
        }

        return zadanie;
    }

    /// <summary>Waga — jedno pole, jedna zmiana (menu podręczne).</summary>
    public Task<TaskItem?> SetPriorityAsync(
        Guid id, Priority priority, CancellationToken ct = default) =>
        ZmienAsync(id, z => z.SetPriority(priority, hlc.Next()), ct);

    /// <summary>Rytm z menu: same rodzaje, bez zaczepienia i pominięć — te mają swój ekran.</summary>
    public Task<TaskItem?> SetRecurrenceAsync(
        Guid id, RecurrenceKind? kind, CancellationToken ct = default) =>
        ZmienAsync(
            id,
            z => z.SetRecurrence(
                kind is { } rodzaj ? new RecurrenceRule(rodzaj) : null, hlc.Next()),
            ct);

    /// <summary>Przeniesienie do projektu albo wyjęcie z niego.</summary>
    public async Task<TaskItem?> SetProjectAsync(
        Guid id, Guid? projectId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } zadanie)
        {
            return null;
        }

        var obszar = zadanie.AreaId ?? (await areas.ActiveAsync(ct)).FirstOrDefault()?.Id;

        if (obszar is not { } identyfikator)
        {
            throw new InvalidOperationException(
                "Nie ma żadnego czynnego obszaru, a zadanie w projekcie musi do któregoś należeć.");
        }

        zadanie.MoveTo(identyfikator, projectId, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return zadanie;
    }

    private async Task<TaskItem?> ZmienAsync(
        Guid id, Action<TaskItem> zmiana, CancellationToken ct)
    {
        if (await tasks.FindAsync(id, ct) is not { } zadanie)
        {
            return null;
        }

        zmiana(zadanie);
        await unitOfWork.SaveChangesAsync(ct);

        return zadanie;
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
        if (await tasks.FindAsync(id, ct) is not { } zadanie)
        {
            return null;
        }

        await ApplyDoDateAsync(zadanie, day, zadanie.AreaId, ct);
        zadanie.SetDoTime(time, hlc.Next());

        await unitOfWork.SaveChangesAsync(ct);
        await OdbijAsync(zadanie, ct);

        return zadanie;
    }

    /// <summary>
    /// Odhaczenie zadania — razem z kolejnym wystąpieniem, jeśli się powtarza (8.4).
    /// </summary>
    public async Task<TaskItem?> CompleteAsync(Guid id, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(id, ct) is not { } zadanie)
        {
            return null;
        }

        // Odhaczenie zostawia ślad na siatce: blok kończy się **teraz**, a zaczyna
        // tyle wcześniej, ile zadanie miało trwać. Zadanie bez godziny znikało dotąd
        // z kalendarza bez śladu, więc wieczorem nie było z czego odczytać, na co
        // poszedł dzień. Godziny wpisanej wcześniej nie ruszamy — to była decyzja,
        // a nie zapis tego, co się stało.
        if (zadanie.DoTime is null && zadanie.State != TaskState.Done)
        {
            await ZapiszPoreWykonaniaAsync(zadanie, ct);
        }

        var nastepne = RecurrenceRunner.Complete(zadanie, clock.Now, hlc.Next);

        if (nastepne is not null)
        {
            tasks.Add(nastepne);
        }

        await unitOfWork.SaveChangesAsync(ct);
        await OdbijAsync(zadanie, ct);

        return nastepne;
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
        if (await tasks.FindAsync(id, ct) is not { } zadanie)
        {
            return null;
        }

        if (zadanie.State != TaskState.Done)
        {
            return zadanie;
        }

        zadanie.Reopen(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
        await OdbijAsync(zadanie, ct);

        return zadanie;
    }

    /// <summary>Blok kończący się teraz, o długości równej oszacowaniu.</summary>
    private async Task ZapiszPoreWykonaniaAsync(TaskItem zadanie, CancellationToken ct)
    {
        var teraz = clock.Now;

        // Do pięciu minut w dół: „skończone o 14:37" jest dokładniejsze, niż bywa prawda.
        var koniec = new TimeOnly(teraz.Hour, teraz.Minute / 5 * 5);
        var dlugosc = TimeSpan.FromMinutes(zadanie.EstimatedMinutes ?? DomyslneMinuty);

        // Początek przycięty do północy: blok ma opisać dzisiaj, a nie sięgnąć wstecz
        // na wczoraj przez zadanie oszacowane na trzy godziny i odhaczone o pierwszej.
        var start = koniec.ToTimeSpan() > dlugosc
            ? TimeOnly.FromTimeSpan(koniec.ToTimeSpan() - dlugosc)
            : TimeOnly.MinValue;

        await ApplyDoDateAsync(zadanie, clock.Today, zadanie.AreaId, ct);
        zadanie.SetDoTime(start, hlc.Next());
    }

    /// <summary>Ile trwa zadanie bez oszacowania (spec 11).</summary>
    private const int DomyslneMinuty = 30;

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
        TaskItem zadanie, DateOnly? doDate, Guid? wybrany, CancellationToken ct)
    {
        // Zadanie odhaczone dostaje sam dzień, bez przejścia stanu. Przejście ustawia
        // „zaplanowane" i tym samym zdejmuje „wykonane" — więc przeciągnięcie
        // wykonanego bloku po siatce wskrzeszało go, a zapis szczegółu odhaczonego
        // zadania cofał odhaczenie. Ruch po siatce poprawia zapis o przeszłości;
        // od cofnięcia decyzji jest zdjęcie ptaszka, osobną czynnością.
        if (zadanie.State == TaskState.Done)
        {
            zadanie.MoveDoDate(doDate, hlc.Next());
            return;
        }

        var obszar = wybrany ?? zadanie.AreaId ?? (await areas.ActiveAsync(ct)).FirstOrDefault()?.Id;

        if (obszar is not { } id)
        {
            throw new InvalidOperationException(
                "Nie ma żadnego czynnego obszaru, a zadanie z dniem wykonania musi do "
                + "któregoś należeć. Załóż albo włącz obszar na ekranie „Obszary i projekty”.");
        }

        if (doDate is { } dzien)
        {
            zadanie.Schedule(id, dzien, hlc.Next());
        }
        else
        {
            zadanie.MakeNext(id, hlc.Next());
        }
    }
}
