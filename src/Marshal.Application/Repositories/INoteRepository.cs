using Marshal.Domain.Notes;

namespace Marshal.Application.Repositories;

public interface INoteRepository
{
    Task<Note?> FindAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Note>> AllAsync(CancellationToken ct = default);

    /// <summary>Przypięte — wizja i zasady, krok zerowy przeglądu (spec 8.3, 1.8).</summary>
    Task<IReadOnlyList<Note>> PinnedAsync(CancellationToken ct = default);

    /// <summary>Szukanie po tytule i treści. Puste zapytanie oddaje wszystko.</summary>
    Task<IReadOnlyList<Note>> SearchAsync(string? query, CancellationToken ct = default);

    void Add(Note note);
}
