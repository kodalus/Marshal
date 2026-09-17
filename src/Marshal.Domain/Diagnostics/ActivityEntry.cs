namespace Marshal.Domain.Diagnostics;

public enum ActivityLevel
{
    /// <summary>Poszło. Liczby w <see cref="ActivityEntry.Outcome"/> mówią ile czego.</summary>
    Ok = 0,

    /// <summary>Nie poszło albo poszło nie tak. Powód w <see cref="ActivityEntry.Detail"/>.</summary>
    Problem = 1,
}

/// <summary>
/// Jedna rzecz, którą aplikacja zrobiła — i co z niej wyszło.
/// </summary>
/// <remarks>
/// <para>
/// Po co: „nic się nie stało" i „zadziałało" wyglądają na ekranie identycznie, a przy
/// pustej siatce kalendarza jest to różnica między brakiem zgody, złym adresem kanału
/// a poprawnym pobraniem nie na te dni. Dziennik rozdziela te przypadki bez podłączania
/// czegokolwiek kablem i bez pytania kogoś, żeby opisał, co widzi.
/// </para>
/// <para>
/// <b>Nie synchronizowany</b> — nie dziedziczy po <c>Entity</c>, więc nie wchodzi ani do
/// dziennika zmian, ani do kopii zapasowej. To zapis tego, co zrobiło **to** urządzenie;
/// przeniesiony na drugie kłamałby o nim.
/// </para>
/// </remarks>
public sealed class ActivityEntry
{
    private ActivityEntry()
    {
        Operation = string.Empty;
        Outcome = string.Empty;
    }

    public ActivityEntry(
        Guid id,
        DateTimeOffset at,
        string operation,
        string outcome,
        ActivityLevel level = ActivityLevel.Ok,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        Id = id;
        At = at;
        Operation = operation.Trim();
        Outcome = (outcome ?? string.Empty).Trim();
        Level = level;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
    }

    public Guid Id { get; private set; }

    public DateTimeOffset At { get; private set; }

    /// <summary>Co robiliśmy: „Kalendarz: pobranie", „Synchronizacja", „Start".</summary>
    public string Operation { get; private set; }

    /// <summary>Co z tego wyszło, najlepiej liczbami: „11 kalendarzy, 52 wydarzenia".</summary>
    public string Outcome { get; private set; }

    public ActivityLevel Level { get; private set; }

    /// <summary>Treść błędu albo szczegóły. Puste, gdy nie ma nic ponad wynik.</summary>
    public string? Detail { get; private set; }
}
