using System.Text.Json;
using Marshal.Domain.Primitives;

namespace Marshal.Domain.Review;

/// <summary>
/// Przegląd tygodniowy w trakcie (spec 8.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stan zapisywany po każdej pojedynczej pozycji, nie po kroku.</b> Przerwanie przez
/// dziecko po czterech minutach to przegląd w 30% ukończony, wznawialny dokładnie w tym
/// miejscu. Przegląd rozłożony na pięć wieczorów jest przeglądem zrobionym, a brak ekranu
/// „zacznij od nowa" jest połową wartości całego mechanizmu.
/// </para>
/// <para>
/// Nieukończony przegląd <b>nie wygasa</b> i nie generuje wyrzutu sumienia w postaci
/// czerwonej plakietki. Nie ma tu żadnego terminu ani licznika dni.
/// </para>
/// <para>
/// Zbiór rozpatrzonych pozycji jest jednym polem z tekstem JSON i scala się jak każde
/// inne pole: nowszy zapis wygrywa w całości. Dwa urządzenia prowadzące ten sam przegląd
/// **równocześnie** zgubiłyby część oznaczeń. Świadomie przyjęte: przegląd robi się
/// w jednym miejscu naraz, a scalanie zbiorów przez sumę wymagałoby własnej reguły
/// scalania dla jednego pola w całym modelu.
/// </para>
/// </remarks>
public sealed class ReviewSession : Entity
{
    private HashSet<Guid>? _processed;

    private ReviewSession()
    {
        ProcessedIdsJson = "[]";
    }

    public ReviewSession(Guid id, DateTimeOffset startedAt, Hlc updatedAt)
        : base(id, startedAt, updatedAt)
    {
        StartedAt = startedAt;
        ProcessedIdsJson = "[]";
    }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Krok kreatora, na którym stanęło. Zerowy to przypięte notatki.</summary>
    public int CurrentStep { get; private set; }

    public string ProcessedIdsJson { get; private set; }

    public bool IsCompleted => CompletedAt is not null;

    private HashSet<Guid> Processed =>
        _processed ??= Parse(ProcessedIdsJson);

    public int ProcessedCount => Processed.Count;

    public bool IsProcessed(Guid id) => Processed.Contains(id);

    public void MarkProcessed(Guid id, Hlc stamp)
    {
        if (!Processed.Add(id))
        {
            return;
        }

        ProcessedIdsJson = JsonSerializer.Serialize(Processed);
        Touch(stamp);
    }

    public void GoTo(int step, Hlc stamp)
    {
        CurrentStep = Math.Max(0, step);
        Touch(stamp);
    }

    public void Complete(DateTimeOffset now, Hlc stamp)
    {
        CompletedAt = now;
        Touch(stamp);
    }

    /// <summary>Zepsuty zapis daje pusty zbiór, a nie wyjątek: przegląd ma się otworzyć
    /// nawet wtedy, gdy wpis przyszedł z nowszej wersji aplikacji (spec 9.4).</summary>
    private static HashSet<Guid> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HashSet<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
