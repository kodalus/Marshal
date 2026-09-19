using System.Text.Json;
using System.Text.Json.Serialization;
using Marshal.Domain.Tasks;

namespace Marshal.Domain.Filters;

/// <summary>
/// Filtr łączony: lista warunków połączonych spójnikiem „i" (spec 11.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Płaska lista, nie drzewo.</b> Pełne wyrażenie logiczne z nawiasami jest mocniejsze
/// i w konstruktorze graficznym praktycznie nieużywane: żeby je złożyć, trzeba myśleć
/// o priorytetach operatorów, a żeby złożone przeczytać — rozwinąć nawiasy w głowie.
/// Stąd podział ról: „albo" mieszka **wewnątrz** warunku (stan: następne albo
/// zaplanowane), „i" **między** warunkami. To pokrywa każdy widok, jaki faktycznie
/// się układa, i daje się przeczytać jednym zdaniem.
/// </para>
/// <para>
/// <b>Jedna kolumna JSON</b>, z tego samego powodu co reguła powtarzania
/// (zob. <see cref="Recurrence.RecurrenceRule"/>): filtr jest jedną decyzją. Scalanie
/// per pole potrafiłoby złożyć widok z połówek dwóch różnych — warunek obszaru
/// z telefonu i warunek stanu z komputera — i dać widok, którego nikt nigdy nie ułożył.
/// </para>
/// </remarks>
public sealed record FilterQuery
{
    /// <summary>
    /// Stany, których filtr nie pokazuje, dopóki sam o nie nie poprosi.
    /// </summary>
    /// <remarks>
    /// Cmentarz i archiwum rosną bez końca i po roku byłyby większe niż wszystko inne
    /// razem. Gdyby wchodziły do każdego widoku domyślnie, każdy filtr trzeba by
    /// zaczynać od odejmowania — a filtr, który zaczyna się od wykluczania, jest
    /// filtrem napisanym od tyłu. Wpisanie stanu do warunku nadal działa dosłownie:
    /// „pokaż wykonane" pokaże wykonane.
    /// </remarks>
    private static readonly TaskState[] Hidden = [TaskState.Done, TaskState.Trashed];

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public FilterQuery(IEnumerable<FilterCondition> conditions)
    {
        // Jeden warunek na pole. Dwa warunki na to samo pole połączone „i" dawałyby
        // zbiór pusty w każdym ciekawym przypadku („stan = Next" i „stan = Waiting"),
        // a konstruktor, w którym da się kliknąć warunek gwarantujący zero wyników,
        // uczy nieufności do całego ekranu.
        Conditions = conditions
            .GroupBy(c => c.Field)
            .Select(g => g.Last())
            .OrderBy(c => c.Field)
            .ToArray();
    }

    public static FilterQuery Empty { get; } = new([]);

    public IReadOnlyList<FilterCondition> Conditions { get; }

    /// <summary>
    /// Filtr bez warunków **nie pasuje do niczego**.
    /// </summary>
    /// <remarks>
    /// Logika mówi co innego — „i" po pustym zbiorze jest prawdą, więc pusty filtr
    /// powinien oddać wszystko. Tyle że wszystko to tu kilkaset pozycji wysypanych na
    /// ekran w chwili, w której nie poproszono jeszcze o nic. Pusty konstruktor znaczy
    /// „nie zaczęłam", a nie „pokaż bazę".
    /// </remarks>
    public bool IsEmpty => Conditions.Count == 0;

    public bool Matches(FilterSubject subject, DateOnly today)
    {
        if (IsEmpty || subject.Task.Deleted)
        {
            return false;
        }

        if (Hidden.Contains(subject.Task.State) && !AsksForState(subject.Task.State))
        {
            return false;
        }

        return Conditions.All(w => w.Matches(subject, today));
    }

    public IEnumerable<TaskItem> Apply(IEnumerable<FilterSubject> subjects, DateOnly today) =>
        subjects.Where(s => Matches(s, today)).Select(s => s.Task);

    public string ToJson() =>
        JsonSerializer.Serialize(
            Conditions
                .Select(c => new Wire(
                    c.Field, c.Values.Count == 0 ? null : c.Values, c.Window, c.MaxMinutes, c.Text))
                .ToArray(),
            Json);

    /// <summary>Zwraca <c>null</c> przy zapisie nieczytelnym, nie rzuca (spec 9.4).</summary>
    public static FilterQuery? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonSerializer.Deserialize<Wire[]>(json, Json) is not { } entries)
            {
                return null;
            }

            // Warunki nieczytelne odpadają pojedynczo. Widok zapisany na urządzeniu
            // z nowszą wersją aplikacji ma tu zadziałać w tej części, którą ta wersja
            // rozumie — zamiast zniknąć w całości.
            var conditions = entries
                .Select(w => FilterCondition.FromWire(w.Field, w.Values, w.Window, w.MaxMinutes, w.Text))
                .OfType<FilterCondition>()
                .ToArray();

            return conditions.Length == 0 ? null : new FilterQuery(conditions);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private bool AsksForState(TaskState state) =>
        Conditions.Any(c => c.Field == FilterField.State && c.Values.Contains(state.ToString()));

    /// <summary>Postać zapisu, oddzielona od typu domenowego — zob. <see cref="Recurrence.RecurrenceRule"/>.</summary>
    private sealed record Wire(
        FilterField Field,
        IReadOnlyList<string>? Values,
        DateWindow? Window,
        int? MaxMinutes,
        string? Text);
}
