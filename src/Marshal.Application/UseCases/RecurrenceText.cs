using System.Globalization;
using System.Text;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>
/// Reguła powtarzania i stan zadania po polsku, jednym zdaniem.
/// </summary>
/// <remarks>
/// <para>
/// Formularz z sześcioma polami da się wypełnić źle i nie zauważyć. Zdanie „co dwa
/// tygodnie, w poniedziałki i czwartki, licząc od wykonania" da się przeczytać
/// i od razu wiedzieć, czy to jest to, o co chodziło. To jest cała rola tego typu:
/// **sprawdzalność wpisanego rytmu bez czekania dwóch tygodni na wynik.**
/// </para>
/// <para>
/// Teksty siedzą tutaj, a nie w warstwie okna, żeby dały się sprawdzić testami —
/// odmiana liczebnika jest w polszczyźnie regułą z wyjątkami i pomyłka w niej nie
/// daje żadnego objawu poza tym, że zdanie brzmi źle.
/// </para>
/// </remarks>
public static class RecurrenceText
{
    private static readonly string[] DayNames =
        ["poniedziałki", "wtorki", "środy", "czwartki", "piątki", "soboty", "niedziele"];

    private static readonly Weekdays[] DayFlags =
    [
        Weekdays.Monday, Weekdays.Tuesday, Weekdays.Wednesday, Weekdays.Thursday,
        Weekdays.Friday, Weekdays.Saturday, Weekdays.Sunday,
    ];

    public static string Describe(RecurrenceRule? rule)
    {
        if (rule is null)
        {
            return "nie powtarza się";
        }

        var sentence = new StringBuilder(Rhythm(rule));

        if (rule.Anchor == RecurrenceAnchor.FromCompletion && rule.Kind is not
            (RecurrenceKind.Daily or RecurrenceKind.EveryNDays))
        {
            sentence.Append(", licząc od wykonania");
        }
        else if (rule.Anchor == RecurrenceAnchor.FromScheduled && rule.Kind is
            (RecurrenceKind.Daily or RecurrenceKind.EveryNDays))
        {
            // Dopisywane tylko wtedy, gdy zaczepienie odbiega od domyślnego dla rodzaju
            // (spec 5.7). Powtarzanie oczywistości w każdym zdaniu zamienia je w szum.
            sentence.Append(", licząc od planu");
        }

        if (rule.Until is { } end)
        {
            sentence.Append(", do ").Append(end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else if (rule.Count is { } count)
        {
            sentence.Append(", jeszcze ").Append(count).Append(' ').Append(Times(count));
        }

        return sentence.ToString();
    }

    /// <summary>Stan zadania po polsku: zaległość, przesunięcia, termin.</summary>
    public static IReadOnlyList<string> Badges(TaskItem task, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(task);
        var labels = new List<string>();

        if (task.CarriedSince is { } od)
        {
            labels.Add($"zaległe od {od:yyyy-MM-dd}");
        }

        // Liczba przesunięć pokazywana dopiero od drugiego: pierwsze zdarza się każdemu
        // i nie niesie informacji. Bez czerwieni i bez wykrzyknika — to licznik, nie kara.
        if (task.RollCount > 1)
        {
            labels.Add($"przesunięte {task.RollCount} {Times(task.RollCount)}");
        }

        if (task.Deadline is { } deadline)
        {
            labels.Add(deadline < today
                ? $"po terminie ({deadline:yyyy-MM-dd})"
                : $"termin {deadline:yyyy-MM-dd}");
        }

        if (task.Recurrence is { } rule)
        {
            labels.Add(Describe(rule));
        }

        return labels;
    }

    private static string Rhythm(RecurrenceRule rule) => rule.Kind switch
    {
        RecurrenceKind.Daily => "codziennie",
        RecurrenceKind.EveryNDays => rule.Interval == 1 ? "codziennie" : $"co {rule.Interval} dni",
        RecurrenceKind.Weekly => Weekly(rule),
        RecurrenceKind.Monthly => Monthly(rule),
        _ => Yearly(rule),
    };

    private static string Weekly(RecurrenceRule rule)
    {
        var days = Days(rule.DaysOfWeek);

        return rule.Interval == 1
            ? $"co tydzień, w {days}"
            : $"co {rule.Interval} {Weeks(rule.Interval)}, w {days}";
    }

    private static string Monthly(RecurrenceRule rule)
    {
        var day = Day(rule.DayOfMonth);

        return rule.Interval == 1
            ? $"co miesiąc, {day}"
            : $"co {rule.Interval} {Months(rule.Interval)}, {day}";
    }

    private static string Yearly(RecurrenceRule rule) =>
        rule.Interval == 1 ? "co rok" : $"co {rule.Interval} {Years(rule.Interval)}";

    private static string Day(int? dayOfMonth) => dayOfMonth switch
    {
        null => "tego samego dnia",
        RecurrenceRule.LastDay => "ostatniego dnia",
        _ => $"{dayOfMonth}. dnia",
    };

    private static string Days(Weekdays set)
    {
        var names = DayFlags
            .Select((flag, i) => (flag, name: DayNames[i]))
            .Where(x => (set & x.flag) != 0)
            .Select(x => x.name)
            .ToList();

        if (names.Count == 0)
        {
            return "wybrane dni";
        }

        return names.Count == 1
            ? names[0]
            : string.Join(", ", names.Take(names.Count - 1)) + " i " + names[^1];
    }

    /// <summary>
    /// Odmiana liczebnika: inna dla 2–4, inna dla reszty, z wyjątkiem nastek.
    /// </summary>
    /// <remarks>
    /// „co 2 tygodnie", ale „co 5 tygodni" i „co 12 tygodni". Reguła wygląda na drobiazg,
    /// a widać ją w każdym zdaniu opisującym rytm.
    /// </remarks>
    private static bool IsFew(int n)
    {
        var tens = n % 100;
        return n % 10 is >= 2 and <= 4 && tens is < 12 or > 14;
    }

    private static string Weeks(int n) => IsFew(n) ? "tygodnie" : "tygodni";

    private static string Months(int n) => IsFew(n) ? "miesiące" : "miesięcy";

    private static string Years(int n) => IsFew(n) ? "lata" : "lat";

    private static string Times(int n) => n == 1 ? "raz" : "razy";
}
