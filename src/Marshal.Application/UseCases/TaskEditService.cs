using Marshal.Application.Abstractions;
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
    IAreaRepository areas)
{
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

        var nastepne = RecurrenceRunner.Complete(zadanie, clock.Now, hlc.Next);

        if (nastepne is not null)
        {
            tasks.Add(nastepne);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return nastepne;
    }

    /// <summary>
    /// Nadanie i zdjęcie dnia wykonania przechodzi przez przejścia stanu, nie przez
    /// samo pole: N8 wymaga, żeby zadanie <c>Scheduled</c> miało datę, a zadanie bez
    /// daty nie było <c>Scheduled</c>.
    /// </summary>
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
        var obszar = wybrany ?? zadanie.AreaId ?? (await areas.ActiveAsync(ct)).FirstOrDefault()?.Id;

        if (obszar is not { } id)
        {
            throw new InvalidOperationException(
                "Nie ma żadnego czynnego obszaru, a zadanie z dniem wykonania musi do "
                + "któregoś należeć. Włącz obszar na ekranie „Obszary”.");
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
