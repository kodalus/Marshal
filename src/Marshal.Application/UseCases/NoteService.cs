using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Notes;

namespace Marshal.Application.UseCases;

/// <summary>
/// Notatki — materiał referencyjny (spec 7, 11).
/// </summary>
public sealed class NoteService(
    INoteRepository notes,
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc)
{
    public Task<IReadOnlyList<Note>> SearchAsync(string? query, CancellationToken ct = default) =>
        notes.SearchAsync(query, ct);

    public Task<IReadOnlyList<Note>> PinnedAsync(CancellationToken ct = default) =>
        notes.PinnedAsync(ct);

    public async Task<Note> CreateAsync(string title, CancellationToken ct = default)
    {
        var note = Note.Create(title, clock.Now, hlc.Next());
        notes.Add(note);
        await unitOfWork.SaveChangesAsync(ct);

        return note;
    }

    /// <summary>
    /// Gałąź „materiał referencyjny" z drzewka przetwarzania (spec 7).
    /// </summary>
    /// <remarks>
    /// Zadanie idzie do kosza, a nie znika: kosz jest nagrobkiem, więc przy scalaniu
    /// widać, że pozycja została rozstrzygnięta, a nie że przepadła. Notatka pamięta,
    /// z czego powstała, żeby dało się zobaczyć skąd się wzięła.
    /// </remarks>
    public async Task<Note?> ConvertToNoteAsync(Guid taskId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(taskId, ct) is not { } task)
        {
            return null;
        }

        var note = Note.FromTask(
            task.Id, task.Title, task.Note, clock.Now, hlc.Next());

        notes.Add(note);
        task.Trash(hlc.Next());

        await unitOfWork.SaveChangesAsync(ct);
        return note;
    }

    public async Task<Note?> SaveAsync(
        Guid id, string title, string? content, bool pinned, CancellationToken ct = default)
    {
        if (await notes.FindAsync(id, ct) is not { } note)
        {
            return null;
        }

        // Każde pole ruszane tylko wtedy, gdy się zmieniło — zob. TaskEditService.
        if (!string.IsNullOrWhiteSpace(title) && title.Trim() != note.Title)
        {
            note.Rename(title, hlc.Next());
        }

        if ((content ?? string.Empty) != note.Content)
        {
            note.SetContent(content, hlc.Next());
        }

        if (pinned != note.IsPinned)
        {
            note.SetPinned(pinned, hlc.Next());
        }

        await unitOfWork.SaveChangesAsync(ct);
        return note;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (await notes.FindAsync(id, ct) is not { } note)
        {
            return;
        }

        // Nagrobek, nie usunięcie fizyczne (spec 5.1).
        note.MarkDeleted(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }
}
