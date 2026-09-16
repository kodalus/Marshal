using CommunityToolkit.Mvvm.ComponentModel;
using Marshal.Domain.Filters;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Pojedyncza wartość włączana i wyłączana — stan, waga, energia, tag.
/// </summary>
/// <remarks>
/// Przełączniki zamiast listy wielokrotnego wyboru: widać naraz, co jest włączone,
/// bez rozwijania czegokolwiek. Przy wyborze „albo" z czterech możliwości lista
/// rozwijana ukrywa dokładnie tę informację, po którą się do niej sięga.
/// </remarks>
public sealed partial class FilterToggle(string label, string value) : ObservableObject
{
    public string Label { get; } = label;

    /// <summary>Nazwa wartości wyliczeniowej albo identyfikator — postać z warunku.</summary>
    public string Value { get; } = value;

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}

/// <summary>
/// Okno czasowe na liście wyboru, z pozycją „bez znaczenia" na początku.
/// </summary>
/// <remarks>
/// Brak warunku jest jedną z możliwości na tej samej liście, a nie osobnym
/// przełącznikiem obok niej — tak jak „nie powtarza się" w rytmie (zob. RepeatChoice).
/// </remarks>
public sealed record WindowChoice(DateWindow? Value, string Label)
{
    public static readonly IReadOnlyList<WindowChoice> All =
    [
        new(null, "bez znaczenia"),
        new(DateWindow.Overdue, "zaległe"),
        new(DateWindow.Today, "dzisiaj"),
        new(DateWindow.ThisWeek, "w tym tygodniu"),
        new(DateWindow.Next30Days, "w ciągu 30 dni"),
        new(DateWindow.Future, "kiedyś później"),
        new(DateWindow.Any, "ustawione"),
        new(DateWindow.None, "nieustawione"),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// Górny limit oszacowania. Nazwa inna niż <see cref="MinutesChoice"/> z widoku
/// „Teraz", bo i pytanie jest inne: tam „ile mam czasu" i odpowiedź jest zawsze,
/// tu „nie dłuższe niż" i brak warunku jest pozycją na liście.
/// </summary>
public sealed record EstimateChoice(int? Value, string Label)
{
    public static readonly IReadOnlyList<EstimateChoice> All =
    [
        new(null, "bez znaczenia"),
        new(15, "kwadrans"),
        new(30, "pół godziny"),
        new(60, "godzina"),
        new(120, "dwie godziny"),
    ];

    public override string ToString() => Label;
}

/// <summary>Obszar albo projekt na liście, z pozycjami „dowolny" i „bez".</summary>
public sealed record ScopeChoice(Guid? Id, string Label)
{
    public static readonly ScopeChoice Any = new(null, "dowolny");

    /// <summary>Zadania bez projektu są dopuszczalne na stałe (spec 5.3), więc muszą być wyszukiwalne.</summary>
    public static readonly ScopeChoice None = new(Guid.Empty, "bez przypisania");

    public override string ToString() => Label;
}
