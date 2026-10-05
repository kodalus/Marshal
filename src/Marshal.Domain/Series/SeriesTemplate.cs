using System.Text.Json;
using System.Text.Json.Serialization;
using Marshal.Domain.Tasks;

namespace Marshal.Domain.Series;

/// <summary>
/// Szablon wystąpienia: to, co serii każdego razu wychodzi takie samo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Jedna kolumna tekstu, nie dwanaście kolumn.</b> Ta sama decyzja, co przy regule
/// powtarzania, i z tego samego powodu: szablon jest <b>jedną decyzją</b> i ma wygrywać
/// albo przegrywać w całości. Rozłożony na kolumny dawałby scalanie per pole, które
/// potrafi złożyć wystąpienie z połówek dwóch różnych postanowień — nazwa z telefonu,
/// godzina z komputera.
/// </para>
/// <para>
/// <b>Tylko to, co w wystąpieniu jest własnością serii.</b> Nie ma tu wyboru na dziś,
/// licznika pominięć, stanu ani historii: to są rzeczy jednego dnia i jednego
/// wystąpienia. Nie ma też terminu i chwili przypomnienia — one liczą się z odstępu
/// od dnia wystąpienia, więc przechodzą przy stawianiu wiersza, a nie leżą w szablonie
/// jako daty.
/// </para>
/// <para>
/// Wyliczenia jako nazwy, nie liczby — szablon siedzi w bazie i w dzienniku zmian jako
/// tekst, a <c>"Fizyczna"</c> da się przeczytać przy diagnostyce, czego o <c>2</c>
/// powiedzieć nie można.
/// </para>
/// </remarks>
public sealed record SeriesTemplate(
    string Title,
    string? Note = null,
    Guid? AreaId = null,
    Guid? ProjectId = null,
    Guid? ParentTaskId = null,
    TimeOnly? DoTime = null,
    int? EstimatedMinutes = null,
    Priority Priority = Priority.None,
    Energy Energy = Energy.Unknown,
    string? Color = null,
    IReadOnlyList<int>? Leads = null,
    int? DeadlineOffsetDays = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly SeriesTemplate Empty = new(string.Empty);

    /// <summary>Szablon zdjęty z zadania, które właśnie dostaje rytm.</summary>
    /// <remarks>
    /// Rytm nadaje się zadaniu już opisanemu — z obszarem, porą i oszacowaniem — więc
    /// szablon bierze się z niego, a nie z pustego formularza. Odstęp terminu liczony
    /// tutaj, bo „zapłacić do 10-go" przy racie robionej 5-go znaczy pięć dni zapasu,
    /// a nie dziesiątego każdego miesiąca.
    /// </remarks>
    public static SeriesTemplate From(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new SeriesTemplate(
            task.Title,
            task.Note,
            task.AreaId,
            task.ProjectId,
            task.ParentTaskId,
            task.DoTime,
            task.EstimatedMinutes,
            task.Priority,
            task.Energy,
            task.Color,
            task.ReminderLeads,
            task is { Deadline: { } deadline, DoDate: { } planned }
                ? deadline.DayNumber - planned.DayNumber
                : null);
    }

    public string ToJson() => JsonSerializer.Serialize(this with { }, Json);

    /// <summary>Zwraca <c>null</c> zamiast rzucać: zapis z nowszej wersji aplikacji nie
    /// może wywrócić scalania (spec 9.4).</summary>
    public static SeriesTemplate? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SeriesTemplate>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
