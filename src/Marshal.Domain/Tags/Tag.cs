using Marshal.Domain.Primitives;

namespace Marshal.Domain.Tags;

/// <summary>
/// Tag — etykieta opcjonalna i wielokrotna, w odróżnieniu od obszaru, który jest
/// wymagany i jednokrotny (spec 5.2).
/// </summary>
public sealed class Tag : Entity
{
    private Tag()
    {
        Name = string.Empty;
    }

    public Tag(Guid id, DateTimeOffset createdAt, Hlc updatedAt, string name, double sortOrder, string? color = null)
        : base(id, createdAt, updatedAt)
    {
        Name = Normalize(name);
        SortOrder = sortOrder;
        Color = color;
    }

    public string Name { get; private set; }

    public string? Color { get; private set; }

    public double SortOrder { get; private set; }

    public void Rename(string name, Hlc stamp)
    {
        Name = Normalize(name);
        Touch(stamp);
    }

    public void SetColor(string? color, Hlc stamp)
    {
        Color = color;
        Touch(stamp);
    }

    /// <summary>Porównanie nazw bez rozróżniania wielkości liter — „Dom" i „dom" to ten sam tag.</summary>
    public static bool SameName(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.CurrentCultureIgnoreCase);

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim().TrimStart('#');
    }
}
