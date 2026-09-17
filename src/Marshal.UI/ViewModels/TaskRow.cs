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

    /// <summary>Zrobione przygaszone — jest, ale nie woła już o uwagę.</summary>
    public double Opacity => IsDone ? 0.5 : 1.0;
}
