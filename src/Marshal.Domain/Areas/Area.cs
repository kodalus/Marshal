using Marshal.Domain.Primitives;

namespace Marshal.Domain.Areas;

/// <summary>
/// Obszar odpowiedzialności — drugi horyzont GTD (spec 5.2). Coś, co się nigdy
/// nie kończy i co się utrzymuje na jakimś poziomie.
/// </summary>
/// <remarks>
/// Obszar to nie tag. Tag jest opcjonalny i wiele-do-wielu; obszar jest wymagany
/// i jednokrotny, bo inaczej nie jest podziałem — projekt w trzech obszarach albo
/// w żadnym psuje każdą liczbę w tabeli równowagi (spec 8.5).
/// </remarks>
public sealed class Area : Entity
{
    public const int DefaultQuietDays = 60;
    public const int DefaultNudgeDaysValue = 7;

    private Area()
    {
        Name = string.Empty;
    }

    public Area(
        Guid id,
        DateTimeOffset createdAt,
        Hlc updatedAt,
        string name,
        double sortOrder,
        int quietDays = DefaultQuietDays,
        int defaultNudgeDays = DefaultNudgeDaysValue,
        string? color = null)
        : base(id, createdAt, updatedAt)
    {
        Name = Normalize(name);
        SortOrder = sortOrder;
        QuietDays = Positive(quietDays, nameof(quietDays));
        DefaultNudgeDays = Positive(defaultNudgeDays, nameof(defaultNudgeDays));
        Color = color;
        IsActive = true;
    }

    public string Name { get; private set; }

    /// <summary>Dziedziczony przez projekty bez własnego koloru.</summary>
    public string? Color { get; private set; }

    public double SortOrder { get; private set; }

    /// <summary>Nieaktywny znika z podziału i z analizy równowagi.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Próg ciszy dla niezmiennika N10 — po ilu dniach bez ruchu zgłosić obszar.</summary>
    public int QuietDays { get; private set; }

    /// <summary>
    /// Domyślny próg ponaglenia „Oczekiwanych" dla zadań tego obszaru. Per obszar,
    /// nie globalnie: siedem dni dla urzędu jest absurdalnie agresywne, dla osoby
    /// w domu za wolne.
    /// </summary>
    public int DefaultNudgeDays { get; private set; }

    public void Rename(string name, Hlc now)
    {
        Name = Normalize(name);
        Touch(now);
    }

    public void SetThresholds(int quietDays, int defaultNudgeDays, Hlc now)
    {
        QuietDays = Positive(quietDays, nameof(quietDays));
        DefaultNudgeDays = Positive(defaultNudgeDays, nameof(defaultNudgeDays));
        Touch(now);
    }

    public void SetActive(bool isActive, Hlc now)
    {
        IsActive = isActive;
        Touch(now);
    }

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static int Positive(int value, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, paramName);
        return value;
    }
}
