using Marshal.Application.UseCases;
using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Zadanie na liście razem z tym, co o nim trzeba wiedzieć z jednego spojrzenia.
/// </summary>
/// <remarks>
/// Etykiety liczone raz, przy budowaniu listy, a nie przy każdym rysowaniu wiersza.
/// Treść etykiet — zob. <see cref="RecurrenceText.Badges"/>: bez czerwieni, bez
/// wykrzykników i bez liczb większych niż 9 (spec 11.1).
/// </remarks>
public sealed record TaskRow(TaskItem Task, string Badges)
{
    public static TaskRow From(TaskItem task, DateOnly today) =>
        new(task, string.Join("  ·  ", RecurrenceText.Badges(task, today)));

    public string Title => Task.Title;

    public bool HasBadges => Badges.Length > 0;

    /// <summary>Odhaczone zostaje na liście — z ptaszkiem, nie przez zniknięcie.</summary>
    /// <remarks>
    /// Znikające zadanie sprawiało, że dzień wyglądał na coraz bardziej pusty w miarę
    /// pracy — czyli dokładnie odwrotnie do tego, co się właśnie stało.
    /// </remarks>
    public bool IsDone => Task.State == TaskState.Done;

    public string Mark => IsDone ? "✓" : string.Empty;

    /// <summary>
    /// Wyrzucone. Na zwykłych listach nie bywa — pojawia się w szukaniu, bo szuka się
    /// także rzeczy, których już nie ma.
    /// </summary>
    public bool IsTrashed => Task.State == TaskState.Trashed;

    /// <summary>
    /// Czy jest jeszcze co odhaczać. Przycisk przy zadaniu zrobionym albo wyrzuconym
    /// obiecywałby czynność, która nic nie znaczy.
    /// </summary>
    public bool CanComplete => !IsDone && !IsTrashed;

    /// <summary>Jednym słowem, co się z tym stało. Puste, gdy nic — czyli gdy wciąż żyje.</summary>
    public string Fate => IsDone ? "zrobione" : IsTrashed ? "w koszu" : string.Empty;

    public bool HasFate => Fate.Length > 0;

    /// <summary>Zamknięte przygaszone — jest, ale nie woła już o uwagę.</summary>
    public double Opacity => IsDone || IsTrashed ? 0.5 : 1.0;
}
