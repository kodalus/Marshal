using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;

namespace Marshal.Domain.Habits;

/// <summary>
/// Jeden dzień nawyku — ile się tego dnia zrobiło.
/// </summary>
/// <remarks>
/// <para>
/// <b>Jeden wpis na dzień, nie jeden na dotknięcie.</b> Liczba rośnie w miejscu, więc
/// osiem szklanek wody to jeden wiersz z ósemką, a nie osiem wierszy. Inaczej rok
/// jednego nawyku to tysiące wpisów do scalenia przy każdej synchronizacji — a pytanie,
/// na które siatka odpowiada, brzmi „czy tego dnia było", a nie „o której".
/// </para>
/// <para>
/// Dzień jest <b>datą, nie chwilą</b>. Nawyk odhaczony o pierwszej w nocy należy do dnia,
/// który właśnie się zaczął — tak jak każdy inny dzień w tej aplikacji liczony jest
/// w strefie z ustawień, a nie w strefie urządzenia.
/// </para>
/// </remarks>
public sealed class HabitMark : Entity
{
    private HabitMark()
    {
    }

    private HabitMark(Guid id, DateTimeOffset createdAt, Hlc updatedAt, Guid habitId, DateOnly day)
        : base(id, createdAt, updatedAt)
    {
        HabitId = habitId;
        Day = day;
        Amount = 0;
    }

    /// <summary>
    /// Wpis dnia o tożsamości <b>wyliczonej</b> z nawyku i dnia.
    /// </summary>
    /// <remarks>
    /// Ta sama odpowiedź, co przy kolejnym wystąpieniu rytmu — i z tego samego powodu.
    /// Telefon i pulpit odhaczające ten sam nawyk tego samego dnia dochodzą do tej samej
    /// rzeczy, więc scalanie składa je w jedną zamiast rozstawiać dwie obok siebie.
    /// Losowany identyfikator dałby dwa wpisy na jeden dzień i serię liczoną podwójnie.
    /// </remarks>
    public static HabitMark On(Guid habitId, DateOnly day, DateTimeOffset now, Hlc stamp) =>
        new(Identity(habitId, day), now, stamp, habitId, day);

    public Guid HabitId { get; private set; }

    public DateOnly Day { get; private set; }

    /// <summary>Ile tego dnia. Przy nawyku na ptaszek zero albo jeden.</summary>
    public int Amount { get; private set; }

    public void Set(int amount, Hlc stamp)
    {
        Amount = Math.Max(0, amount);
        Touch(stamp);
    }

    public void Add(int more, Hlc stamp) => Set(Amount + more, stamp);

    /// <summary>Czy dzień się policzył przy danym progu.</summary>
    public bool Counts(int? target) => Amount >= Math.Max(1, target ?? 1);

    internal static Guid Identity(Guid habitId, DateOnly day) =>
        OccurrenceId.For(habitId, day);
}
