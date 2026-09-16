namespace Marshal.Domain.Recurrence;

public enum RecurrenceKind
{
    Daily,
    EveryNDays,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>Od czego liczyć następne wystąpienie (spec 5.7).</summary>
public enum RecurrenceAnchor
{
    /// <summary>
    /// Od daty zaplanowanej. „Co poniedziałek śmieci": odhaczenie we wtorek nie
    /// przesuwa kolejnego poniedziałku.
    /// </summary>
    FromScheduled,

    /// <summary>
    /// Od faktycznego wykonania. „Co 3 dni podlewanie": odhaczenie z dwudniowym
    /// opóźnieniem przesuwa cały rytm.
    /// </summary>
    FromCompletion,
}

/// <summary>Co z niewykonanym wystąpieniem (spec 5.7).</summary>
public enum OnMissed
{
    /// <summary>Przepada. Dla rzeczy bez wartości po terminie.</summary>
    Skip,

    /// <summary>Zostaje jako zaległe, kolejne wystąpienie je zastępuje. Domyślne.</summary>
    Carry,

    /// <summary>
    /// Każde niewykonane zostaje osobno. Trzy nieodhaczone treningi to trzy pozycje.
    /// </summary>
    /// <remarks>
    /// Jedyna ścieżka w całym modelu, która potrafi wyprodukować stertę, a sterta jest
    /// dokładnie tym, przed czym ma chronić zasada 1.2. Zostaje w modelu, ale interfejs
    /// wersji 1 jej nie proponuje (spec 13.2 pkt 1).
    /// </remarks>
    Accumulate,
}

[Flags]
public enum Weekdays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,

    Workdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Workdays | Weekend,
}

public static class WeekdaysExtensions
{
    public static Weekdays ToFlag(this DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Weekdays.Monday,
        DayOfWeek.Tuesday => Weekdays.Tuesday,
        DayOfWeek.Wednesday => Weekdays.Wednesday,
        DayOfWeek.Thursday => Weekdays.Thursday,
        DayOfWeek.Friday => Weekdays.Friday,
        DayOfWeek.Saturday => Weekdays.Saturday,
        _ => Weekdays.Sunday,
    };

    public static bool Includes(this Weekdays set, DayOfWeek day) => (set & day.ToFlag()) != 0;
}
