using Marshal.Application.Repositories;
using Marshal.Domain.Notes;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class NoteRepository(MarshalDbContext db) : INoteRepository
{
    public async Task<Note?> FindAsync(Guid id, CancellationToken ct = default) =>
        await db.Notes.FirstOrDefaultAsync(n => n.Id == id && !n.Deleted, ct);

    public async Task<IReadOnlyList<Note>> AllAsync(CancellationToken ct = default) =>
        await Zywe().OrderByDescending(n => n.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Note>> PinnedAsync(CancellationToken ct = default) =>
        await Zywe()
            .Where(n => n.IsPinned)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Note>> SearchAsync(
        string? query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await AllAsync(ct);
        }

        var szukane = query.Trim();

        // Szukanie po tytule i po treści. Bez indeksu pełnotekstowego: przy kilkuset
        // notatkach przeglądanie po kolei jest niezauważalne, a indeks pełnotekstowy
        // w SQLite to osobna tabela, którą trzeba by utrzymywać w zgodzie przy każdej
        // synchronizacji.
        return await Zywe()
            .Where(n => EF.Functions.Like(n.Title, $"%{szukane}%")
                     || EF.Functions.Like(n.Content, $"%{szukane}%"))
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    public void Add(Note note) => db.Notes.Add(note);

    private IQueryable<Note> Zywe() => db.Notes.Where(n => !n.Deleted);
}
