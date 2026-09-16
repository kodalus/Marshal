using Marshal.Domain.Attachments;

namespace Marshal.Application.Repositories;

public interface IAttachmentRepository
{
    Task<Attachment?> FindAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Attachment>> ForTaskAsync(Guid taskId, CancellationToken ct = default);

    Task<IReadOnlyList<Attachment>> ForNoteAsync(Guid noteId, CancellationToken ct = default);

    /// <summary>Wszystkie skróty, które są w bazie — do sprawdzenia, czego brakuje w składnicy.</summary>
    Task<IReadOnlyList<string>> AllHashesAsync(CancellationToken ct = default);

    void Add(Attachment attachment);
}
