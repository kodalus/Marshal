using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Notes;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class NoteRepository(MarshalDbContext db, IDbQueue? queue = null)
    : INoteRepository
{
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    public async Task<Note?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.Notes.FirstOrDefaultAsync(n => n.Id == id && !n.Deleted, ct), ct);

    public async Task<IReadOnlyList<Note>> AllAsync(CancellationToken ct = default) =>
        await _queue.RunAsync(() => Live().OrderByDescending(n => n.CreatedAt).ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Note>> PinnedAsync(CancellationToken ct = default) =>
        await _queue.RunAsync(() => Live()
            .Where(n => n.IsPinned)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Note>> SearchAsync(
        string? query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await AllAsync(ct);
        }

        var wanted = query.Trim();

        // Szukanie po tytule i po treści. Bez indeksu pełnotekstowego: przy kilkuset
        // notatkach przeglądanie po kolei jest niezauważalne, a indeks pełnotekstowy
        // w SQLite to osobna tabela, którą trzeba by utrzymywać w zgodzie przy każdej
        // synchronizacji.
        return await _queue.RunAsync(() => Live()
            .Where(n => EF.Functions.Like(n.Title, $"%{wanted}%")
                     || EF.Functions.Like(n.Content, $"%{wanted}%"))
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct), ct);
    }

    public void Add(Note note) => db.Notes.Add(note);

    private IQueryable<Note> Live() => db.Notes.Where(n => !n.Deleted);
}
