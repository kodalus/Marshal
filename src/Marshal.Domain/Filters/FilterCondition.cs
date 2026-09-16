using Marshal.Domain.Tasks;

namespace Marshal.Domain.Filters;

/// <summary>
/// Pojedynczy warunek filtra: jedno pole i to, co ma w nim być (spec 11.5).
/// </summary>
/// <remarks>
/// <para>
/// Wartości trzymane jako teksty, nie jako typy. Filtr siedzi w bazie i w dzienniku
/// zmian jako JSON, a wartość, która przestała istnieć — skasowany tag, obszar
/// z drugiego urządzenia — ma **przestać pasować**, a nie wywrócić odczyt. Tekst
/// nieznanej wartości po prostu nie trafia na nic.
/// </para>
/// <para>
/// Wewnątrz jednego warunku obowiązuje „albo": stan „następne albo zaplanowane" to
/// jeden warunek o dwóch wartościach. Między warunkami obowiązuje „i" — zob.
/// <see cref="FilterQuery"/>.
/// </para>
/// </remarks>
public sealed record FilterCondition
{
    private FilterCondition(
        FilterField field,
        IReadOnlyList<string> values,
        DateWindow? window,
        int? maxMinutes,
        string? text)
    {
        Field = field;
        Values = values;
        Window = window;
        MaxMinutes = maxMinutes;
        Text = text;
    }

    public FilterField Field { get; }

    /// <summary>Nazwy wartości wyliczeniowych albo identyfikatory. Puste dla pozostałych pól.</summary>
    public IReadOnlyList<string> Values { get; }

    public DateWindow? Window { get; }

    public int? MaxMinutes { get; }

    public string? Text { get; }

    public static FilterCondition States(params TaskState[] states) =>
        Set(FilterField.State, states.Select(s => s.ToString()));

    public static FilterCondition Priorities(params Priority[] priorities) =>
        Set(FilterField.Priority, priorities.Select(p => p.ToString()));

    public static FilterCondition Energies(params Energy[] energies) =>
        Set(FilterField.Energy, energies.Select(e => e.ToString()));

    /// <summary><see cref="Guid.Empty"/> znaczy „bez obszaru".</summary>
    public static FilterCondition Areas(params Guid[] ids) => Ids(FilterField.Area, ids);

    /// <summary><see cref="Guid.Empty"/> znaczy „bez projektu".</summary>
    public static FilterCondition Projects(params Guid[] ids) => Ids(FilterField.Project, ids);

    /// <summary><see cref="Guid.Empty"/> znaczy „bez tagów".</summary>
    public static FilterCondition Tags(params Guid[] ids) => Ids(FilterField.Tag, ids);

    public static FilterCondition Deadline(DateWindow window) =>
        new(FilterField.Deadline, [], window, null, null);

    public static FilterCondition DoDate(DateWindow window) =>
        new(FilterField.DoDate, [], window, null, null);

    /// <summary>„Mam kwadrans" — zadania o oszacowaniu nie większym niż podane.</summary>
    public static FilterCondition Estimate(int maxMinutes)
    {
        if (maxMinutes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMinutes), "Limit minut musi być dodatni.");
        }

        return new FilterCondition(FilterField.Estimate, [], null, maxMinutes, null);
    }

    public static FilterCondition Contains(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new FilterCondition(FilterField.Text, [], null, null, text.Trim());
    }

    /// <summary>
    /// Odczyt z postaci zapisanej. Zwraca <c>null</c> zamiast rzucać — warunek
    /// nieczytelny ma zniknąć z filtra, a nie unieważnić cały zapisany widok (spec 9.4).
    /// </summary>
    internal static FilterCondition? FromWire(
        FilterField field, IReadOnlyList<string>? values, DateWindow? window, int? minutes, string? text)
    {
        var wartosci = values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() ?? [];

        return field switch
        {
            FilterField.State or FilterField.Priority or FilterField.Energy
                or FilterField.Area or FilterField.Project or FilterField.Tag =>
                    wartosci.Length == 0
                        ? null
                        : new FilterCondition(field, wartosci, null, null, null),

            FilterField.Deadline or FilterField.DoDate =>
                window is null ? null : new FilterCondition(field, [], window, null, null),

            FilterField.Estimate =>
                minutes is null or < 1 ? null : new FilterCondition(field, [], null, minutes, null),

            FilterField.Text =>
                string.IsNullOrWhiteSpace(text)
                    ? null
                    : new FilterCondition(field, [], null, null, text.Trim()),

            _ => null,
        };
    }

    /// <summary>Czy zadanie spełnia ten warunek.</summary>
    public bool Matches(FilterSubject subject, DateOnly today) => Field switch
    {
        FilterField.State => Values.Contains(subject.Task.State.ToString()),
        FilterField.Priority => Values.Contains(subject.Task.Priority.ToString()),
        FilterField.Energy => Values.Contains(subject.Task.Energy.ToString()),
        FilterField.Area => MatchesId(subject.Task.AreaId),
        FilterField.Project => MatchesId(subject.Task.ProjectId),
        FilterField.Tag => MatchesTags(subject.Tags),
        FilterField.Deadline => InWindow(subject.Task.Deadline, today),
        FilterField.DoDate => InWindow(subject.Task.DoDate, today),
        FilterField.Estimate => subject.Task.EstimatedMinutes is { } m && m <= MaxMinutes,
        FilterField.Text => MatchesText(subject.Task),
        _ => false,
    };

    private static FilterCondition Set(FilterField field, IEnumerable<string> values)
    {
        var wartosci = values.Distinct().ToArray();

        if (wartosci.Length == 0)
        {
            throw new ArgumentException("Warunek bez żadnej wartości nie zawęża niczego.", nameof(values));
        }

        return new FilterCondition(field, wartosci, null, null, null);
    }

    private static FilterCondition Ids(FilterField field, Guid[] ids) =>
        Set(field, ids.Select(i => i.ToString()));

    private bool MatchesId(Guid? id) => Values.Contains((id ?? Guid.Empty).ToString());

    private bool MatchesTags(IReadOnlyCollection<Guid> tags) =>
        tags.Count == 0
            ? Values.Contains(Guid.Empty.ToString())
            : tags.Any(t => Values.Contains(t.ToString()));

    private bool MatchesText(TaskItem task) =>
        task.Title.Contains(Text!, StringComparison.CurrentCultureIgnoreCase)
        || (task.Note?.Contains(Text!, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private bool InWindow(DateOnly? value, DateOnly today) => Window switch
    {
        DateWindow.None => value is null,
        DateWindow.Any => value is not null,
        DateWindow.Overdue => value is { } d && d < today,
        DateWindow.Today => value == today,
        DateWindow.ThisWeek => value is { } d && d >= today && d <= today.AddDays(6),
        DateWindow.Next30Days => value is { } d && d >= today && d <= today.AddDays(30),
        DateWindow.Future => value is { } d && d > today,
        _ => false,
    };
}
