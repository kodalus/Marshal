using Marshal.Domain.Primitives;

namespace Marshal.Domain.Tags;

/// <summary>
/// Powiązanie zadania z tagiem, jako osobny rekord z własnym znacznikiem zmiany
/// i własnym nagrobkiem.
/// </summary>
/// <remarks>
/// EF Core potrafi obsłużyć relację wiele-do-wielu niejawnie, tworząc tabelę pośrednią
/// bez własnych kolumn. Tutaj to nie wystarcza: taka tabela nie ma znacznika zegara
/// logicznego ani nagrobka, więc **zdjęcia tagu nie dałoby się zsynchronizować** —
/// drugie urządzenie nie odróżniłoby „usunięto powiązanie" od „jeszcze go nie znam"
/// i przywróciłoby je przy najbliższym scaleniu (spec 5.1).
/// </remarks>
public sealed class TaskTag : Entity
{
    private TaskTag()
    {
    }

    public TaskTag(Guid id, DateTimeOffset createdAt, Hlc updatedAt, Guid taskId, Guid tagId)
        : base(id, createdAt, updatedAt)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Brak zadania.", nameof(taskId));
        if (tagId == Guid.Empty) throw new ArgumentException("Brak tagu.", nameof(tagId));

        TaskId = taskId;
        TagId = tagId;
    }

    public Guid TaskId { get; private set; }

    public Guid TagId { get; private set; }
}
