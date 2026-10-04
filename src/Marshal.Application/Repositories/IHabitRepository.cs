using Marshal.Domain.Habits;

namespace Marshal.Application.Repositories;

public interface IHabitRepository
{
    /// <summary>Nawyki na liście — żywe i nieodłożone, w swojej kolejności.</summary>
    Task<IReadOnlyList<Habit>> ListAsync(bool archived = false, CancellationToken ct = default);

    Task<Habit?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Wszystkie dni, w których cokolwiek było.
    /// </summary>
    /// <remarks>
    /// Bez zakresu i to jest decyzja. Siatka pokazuje ostatnie kilkanaście tygodni, ale
    /// seria, rekord i licznik liczą się <b>od początku</b> — a licząc je z okna,
    /// mówiłyby „rekord: 17 tygodni" u kogoś, kto ciągnie to drugi rok. Dni, w których
    /// nic nie było, nie mają wiersza, więc rok sześciu nawyków to najwyżej dwa tysiące
    /// wierszy: tyle przejrzeć po kolei jest niezauważalne.
    /// </remarks>
    Task<IReadOnlyList<HabitMark>> MarksAsync(CancellationToken ct = default);

    Task<HabitMark?> MarkAsync(Guid habitId, DateOnly day, CancellationToken ct = default);

    void Add(Habit habit);

    void Add(HabitMark mark);
}
