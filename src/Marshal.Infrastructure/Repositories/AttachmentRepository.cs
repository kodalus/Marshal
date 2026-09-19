using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Attachments;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class AttachmentRepository(MarshalDbContext db, IDbQueue? queue = null)
    : IAttachmentRepository
{
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    public async Task<Attachment?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Attachments.FirstOrDefaultAsync(a => a.Id == id && !a.Deleted, ct), ct);

    public async Task<IReadOnlyList<Attachment>> ForTaskAsync(
        Guid taskId, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Attachments
            .Where(a => a.TaskId == taskId && !a.Deleted)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Attachment>> ForNoteAsync(
        Guid noteId, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Attachments
            .Where(a => a.NoteId == noteId && !a.Deleted)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<string>> AllHashesAsync(CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Attachments
            .Where(a => !a.Deleted)
            .Select(a => a.Sha256)
            .Distinct()
            .ToListAsync(ct), ct);

    public void Add(Attachment attachment) => db.Attachments.Add(attachment);
}
