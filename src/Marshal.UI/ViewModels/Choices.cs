using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Rytm na liście wyboru. <see cref="RecurrenceKind"/> z dopisaną pozycją „bez
/// powtarzania" — bo z punktu widzenia okna brak rytmu jest jedną z możliwości,
/// a nie osobnym przełącznikiem obok listy.
/// </summary>
public sealed record RepeatChoice(RecurrenceKind? Kind, string Label)
{
    public static readonly IReadOnlyList<RepeatChoice> All =
    [
        new(null, "nie powtarza się"),
        new(RecurrenceKind.Daily, "codziennie"),
        new(RecurrenceKind.EveryNDays, "co kilka dni"),
        new(RecurrenceKind.Weekly, "co tydzień"),
        new(RecurrenceKind.Monthly, "co miesiąc"),
        new(RecurrenceKind.Yearly, "co rok"),
    ];

    public override string ToString() => Label;
}

public sealed record AnchorChoice(RecurrenceAnchor Value, string Label)
{
    public static readonly IReadOnlyList<AnchorChoice> All =
    [
        new(RecurrenceAnchor.FromScheduled, "od zaplanowanej daty"),
        new(RecurrenceAnchor.FromCompletion, "od wykonania"),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// Co z pominiętym wystąpieniem. <see cref="OnMissed.Accumulate"/> celowo nieobecne
/// (spec 13.2 pkt 1): jest jedyną ścieżką, która potrafi wyprodukować stertę, a wybór,
/// którego nie widać, nie kusi. W modelu zostaje — zapisy z drugiego urządzenia albo
/// z późniejszej wersji aplikacji działają normalnie.
/// </summary>
public sealed record MissedChoice(OnMissed Value, string Label)
{
    public static readonly IReadOnlyList<MissedChoice> All =
    [
        new(OnMissed.Carry, "zostaje jako zaległe"),
        new(OnMissed.Skip, "przepada"),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// Poziom energii. „Nieokreślona" jest pierwsza i domyślna, bo brak decyzji jest
/// stanem wyjściowym — a zadanie nieokreślone przechodzi przy każdym poziomie.
/// </summary>
public sealed record EnergyLevelChoice(Energy Value, string Label)
{
    public static readonly IReadOnlyList<EnergyLevelChoice> All =
    [
        new(Energy.Unknown, "nieokreślona"),
        new(Energy.Low, "resztki wystarczą"),
        new(Energy.Medium, "średnia"),
        new(Energy.High, "pełna"),
    ];

    public override string ToString() => Label;
}

public sealed record PriorityChoice(Priority Value, string Label)
{
    public static readonly IReadOnlyList<PriorityChoice> All =
    [
        new(Priority.None, "bez wagi"),
        new(Priority.Low, "niska"),
        new(Priority.Normal, "zwykła"),
        new(Priority.High, "wysoka"),
    ];

    public override string ToString() => Label;
}
