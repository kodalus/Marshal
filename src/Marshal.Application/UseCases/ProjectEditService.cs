using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;

namespace Marshal.Application.UseCases;

/// <summary>
/// Zmiany na ekranie „Projekty": barwa obszaru i projektu oraz usunięcie projektu.
/// </summary>
/// <remarks>
/// Barwa jest tu, a nie przy zadaniu, bo ustawia się ją raz na obszarze i schodzi
/// w dół sama. Gdyby kolorować zadania po jednym, kolor przestałby cokolwiek znaczyć
/// po pierwszym tygodniu — a ma odpowiadać na pytanie „czym się dziś zajmowałam",
/// zadane jednym spojrzeniem na siatkę.
/// </remarks>
public sealed class ProjectEditService(
    IProjectRepository projects,
    IAreaRepository areas,
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IHlcSource hlc)
{
    public async Task SetAreaColorAsync(Guid areaId, string? color, CancellationToken ct = default)
    {
        if (await areas.FindAsync(areaId, ct) is not { } obszar)
        {
            return;
        }

        obszar.SetColor(color, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task SetProjectColorAsync(Guid projectId, string? color, CancellationToken ct = default)
    {
        if (await projects.FindAsync(projectId, ct) is not { } projekt)
        {
            return;
        }

        projekt.SetColor(color, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Czy projekt da się usunąć: tylko taki, który nie ma podprojektów ani żywych zadań.
    /// </summary>
    /// <remarks>
    /// Usunięcie projektu z zawartością nie ma dobrej odpowiedzi: skasowanie zadań
    /// razem z nim niszczy pracę, zostawienie ich robi sieroty niewidoczne na żadnym
    /// ekranie. Zamiast wybierać za użytkowniczkę, blokujemy usunięcie i zostawiamy
    /// „zamknij projekt", które nie kłamie o tym, co się stało z zadaniami.
    /// </remarks>
    public async Task<string?> WhyCannotDeleteAsync(Guid projectId, CancellationToken ct = default)
    {
        var wszystkie = await projects.AllAsync(ct);

        if (wszystkie.Any(p => p.ParentProjectId == projectId))
        {
            return "Projekt ma podprojekty — najpierw usuń albo odepnij je.";
        }

        var zadania = await tasks.ByProjectAsync(projectId, ct);

        return zadania.Count > 0
            ? $"Projekt ma zadania ({zadania.Count}) — przenieś je albo zamknij projekt."
            : null;
    }

    /// <summary>Usunięcie projektu. Nagrobkiem, nie kasowaniem — inaczej wróciłby przy scaleniu.</summary>
    public async Task<string?> DeleteProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        if (await WhyCannotDeleteAsync(projectId, ct) is { } przeszkoda)
        {
            return przeszkoda;
        }

        if (await projects.FindAsync(projectId, ct) is not { } projekt)
        {
            return null;
        }

        projekt.MarkDeleted(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return null;
    }
}
