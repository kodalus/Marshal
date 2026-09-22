using Marshal.Application.UseCases;
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

/// <summary>Kiedy rytm się kończy — postać z okna, nie z modelu.</summary>
/// <remarks>
/// Model niesie to dwoma polami, z których każde może być puste: dzień ostatniego
/// wystąpienia i liczba pozostałych. Okno pyta o to raz, bo to jedno pytanie, a dwa
/// pola obok siebie pozwalałyby wpisać obie odpowiedzi naraz — i nie dałoby się
/// powiedzieć, która obowiązuje.
/// </remarks>
public enum RepeatEnd
{
    Never,
    AfterCount,
    OnDate,
}

public sealed record EndChoice(RepeatEnd Value, string Label)
{
    public static readonly IReadOnlyList<EndChoice> All =
    [
        new(RepeatEnd.Never, "bez końca"),
        new(RepeatEnd.AfterCount, "po tylu wystąpieniach"),
        new(RepeatEnd.OnDate, "do dnia"),
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

/// <summary>
/// Miejsce zadania: obszar albo projekt w obszarze — jedna lista zamiast dwóch pól.
/// </summary>
/// <remarks>
/// Dwa osobne pola, „obszar” i „projekt”, dają cztery stany, z których jeden jest
/// sprzeczny: projekt z jednego obszaru wybrany przy drugim. Jedno drzewko nie pozwala
/// takiego stanu wyprodukować, bo wybór projektu **jest** wyborem jego obszaru.
/// Wcięcie robi z listy drzewko: ekran „Projekty” pokazuje tę samą hierarchię i ta
/// sama hierarchia ma wyglądać tu tak samo.
/// </remarks>
public sealed record PlacementChoice(Guid AreaId, Guid? ProjectId, string Label, double Indent)
{
    public static PlacementChoice From(ProjectRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new PlacementChoice(
            row.AreaId,
            row.IsArea ? null : row.Id,
            row.Label,
            row.Indent);
    }

    public override string ToString() => Label;
}

/// <summary>
/// Codzienna kopia na liście wyboru przy przywracaniu.
/// </summary>
/// <remarks>
/// Podpis składany tutaj, a nie w oknie: sam <see cref="DateOnly"/> na liście
/// rysowałby się tak, jak każe ustawienie języka systemu — a wtedy ta jedna lista
/// mówiłaby o dacie inaczej niż reszta aplikacji.
/// </remarks>
public sealed record RestoreChoice(DateOnly Day, string Label)
{
    public static RestoreChoice Of(DateOnly day, DateOnly today) => new(
        day,
        day == today ? $"dzisiaj ({day:dd.MM.yyyy})"
            : day == today.AddDays(-1) ? $"wczoraj ({day:dd.MM.yyyy})"
            : $"{day:dd.MM.yyyy}");

    public override string ToString() => Label;
}
