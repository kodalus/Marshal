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
    Priority Priority);

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
    IClock clock)
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

        if (edit.Recurrence != zadanie.Recurrence)
        {
            zadanie.SetRecurrence(edit.Recurrence, hlc.Next());
        }

        if (edit.DoDate != zadanie.DoDate)
        {
            ApplyDoDate(zadanie, edit.DoDate);
        }

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
    private void ApplyDoDate(TaskItem zadanie, DateOnly? doDate)
    {
        if (doDate is { } dzien && zadanie.AreaId is { } obszar)
        {
            zadanie.Schedule(obszar, dzien, hlc.Next());
        }
        else if (doDate is null && zadanie.AreaId is { } obszarBezDaty)
        {
            zadanie.MakeNext(obszarBezDaty, hlc.Next());
        }
    }
}
