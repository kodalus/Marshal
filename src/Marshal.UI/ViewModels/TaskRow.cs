using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
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
    /// <summary>
    /// Wiersz listy. Reguła podawana z zewnątrz, bo leży w serii, a nie w zadaniu.
    /// </summary>
    /// <remarks>
    /// Bez podania wystąpieniu serii zostaje etykieta „w serii" — prawdziwa, ale uboższa
    /// niż „co poniedziałek". Lista, która ma skąd wziąć reguły, podaje je i wtedy
    /// etykieta brzmi tak jak zawsze.
    /// </remarks>
    public static TaskRow From(
        TaskItem task, DateOnly today, IReadOnlyDictionary<Guid, RecurrenceRule>? rules = null)
    {
        var rule = task.SeriesId is { } mine && rules is not null
            && rules.TryGetValue(mine, out var found)
                ? found
                : null;

        return new TaskRow(task, string.Join("  ·  ", RecurrenceText.Badges(task, today, rule)));
    }

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

    /// <summary>
    /// Jednym słowem, co się z tym stało. Puste, gdy nic — czyli gdy wciąż żyje.
    /// </summary>
    /// <remarks>
    /// Dzień z cyklu idzie do kosza jedyną drogą, jaką ma — „pomiń to wystąpienie" —
    /// i „w koszu" mówi o nim nieprawdę: nikt go nie wyrzucił, ten jeden raz się nie
    /// odbył. W archiwum było to jedyne, co o takim dniu stało, i brzmiało jak pomyłka.
    /// Słowo to samo, co w menu, którym się to robi: „odwołane" znaczy w tym programie
    /// coś innego — odwołanie wystąpienia, którego jeszcze nie ma, zapisane w regule.
    /// </remarks>
    public string Fate => IsDone
        ? "zrobione"
        : IsTrashed
            ? (Task.SeriesId is null ? "w koszu" : "pominięte")
            : string.Empty;

    /// <summary>Czy da się to wyjąć z kosza.</summary>
    /// <remarks>
    /// Przy wykonanym nie: zdjęcie ptaszka jest osobną czynnością i stoi przy zadaniu
    /// na liście, a nie w archiwum. Tu chodzi o cofnięcie wyrzucenia.
    /// </remarks>
    public bool CanRevive => IsTrashed;

    public bool HasFate => Fate.Length > 0;

    /// <summary>Zamknięte przygaszone — jest, ale nie woła już o uwagę.</summary>
    public double Opacity => IsDone || IsTrashed ? 0.5 : 1.0;
}
