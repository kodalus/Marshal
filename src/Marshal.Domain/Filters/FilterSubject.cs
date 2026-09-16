using Marshal.Domain.Tasks;

namespace Marshal.Domain.Filters;

/// <summary>
/// Zadanie razem z tym, czego samo o sobie nie wie — na potrzeby sprawdzania warunków.
/// </summary>
/// <remarks>
/// Tagi są osobnym agregatem (zob. <see cref="Tags.TaskTag"/>), więc zadanie ich nie
/// nosi. Podanie ich obok zamiast doczytywania w środku trzyma sprawdzanie warunków
/// **czystą funkcją**: nie sięga do bazy, więc daje się przetestować na gołych obiektach
/// i da się ją uruchomić na liście wczytanej raz, a nie raz na zadanie.
/// </remarks>
public sealed record FilterSubject(TaskItem Task, IReadOnlyCollection<Guid> Tags)
{
    public FilterSubject(TaskItem task)
        : this(task, [])
    {
    }
}
