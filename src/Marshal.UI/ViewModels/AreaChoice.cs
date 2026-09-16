using Marshal.Domain.Areas;

namespace Marshal.UI.ViewModels;

/// <summary>Obszar na liście wyboru przy przetwarzaniu.</summary>
public sealed record AreaChoice(Guid Id, string Name)
{
    public static AreaChoice From(Area area) => new(area.Id, area.Name);

    public override string ToString() => Name;
}
