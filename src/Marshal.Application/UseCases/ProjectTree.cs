using Marshal.Domain.Areas;
using Marshal.Domain.Projects;

namespace Marshal.Application.UseCases;

/// <summary>Wiersz drzewa: obszar albo projekt, z głębokością zagnieżdżenia.</summary>
public sealed record ProjectRow(
    Guid Id, string Label, int Depth, bool IsArea, bool IsBlocked = false)
{
    /// <summary>Wcięcie w punktach. Liczba, nie typ interfejsu — warstwa aplikacji nie zna Avalonii.</summary>
    public double Indent => Depth * 20.0;

    /// <summary>N1 — projekt bez następnej akcji. Napisem, nie kolorem (spec 11.1).</summary>
    public string Marker => IsBlocked ? "brak następnej akcji" : string.Empty;
}

/// <summary>
/// Spłaszcza obszary i projekty do jednej listy wierszy: obszar → cel → projekt → …
/// (spec 11, ekran „Projekty").
/// </summary>
public static class ProjectTree
{
    /// <param name="blocked">
    /// Projekty bez następnej akcji (N1). Wyróżnienie robi się tutaj, bo drzewo i tak
    /// przechodzi po wszystkich projektach — osobne przejście po liście tylko po to,
    /// żeby dopisać jedną flagę, byłoby drugim miejscem do rozjechania się z pierwszym.
    /// </param>
    public static IReadOnlyList<ProjectRow> Build(
        IReadOnlyList<Area> areas,
        IReadOnlyList<Project> projects,
        IReadOnlySet<Guid>? blocked = null)
    {
        var rows = new List<ProjectRow>();
        var byParent = projects
            .GroupBy(p => p.ParentProjectId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.OrderBy(p => p.SortOrder).ThenBy(p => p.Outcome).ToList());

        var znane = projects.Select(p => p.Id).ToHashSet();

        // Jeden zbiór na całe drzewo, nie na gałąź: po scaleniu dwóch urządzeń projekt
        // może trafić pod rodzica z innego obszaru i bez tego pojawiłby się dwa razy.
        var odwiedzone = new HashSet<Guid>();

        foreach (var area in areas.OrderBy(a => a.SortOrder))
        {
            rows.Add(new ProjectRow(area.Id, area.Name, 0, IsArea: true));

            // Korzeniem w obszarze jest projekt bez rodzica **albo taki, którego rodzic
            // nie dotarł**. Przy synchronizacji plikowej zmiany przychodzą w kolejności
            // zapisu, nie zależności, więc podprojekt potrafi wyprzedzić swój cel.
            // Ukrycie go do czasu przyjścia rodzica oznaczałoby, że zadanie istnieje,
            // ale nie widać go nigdzie.
            var korzenie = projects
                .Where(p => p.AreaId == area.Id
                         && (p.ParentProjectId is null || !znane.Contains(p.ParentProjectId.Value)))
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.Outcome);

            foreach (var projekt in korzenie)
            {
                Dopisz(projekt, depth: 1, rows, byParent, odwiedzone, blocked);
            }
        }

        return rows;
    }

    private static void Dopisz(
        Project projekt,
        int depth,
        List<ProjectRow> rows,
        Dictionary<Guid, List<Project>> byParent,
        HashSet<Guid> odwiedzone,
        IReadOnlySet<Guid>? blocked)
    {
        // Zabezpieczenie przed cyklem: przy scalaniu dwóch urządzeń da się otrzymać
        // projekt będący własnym przodkiem, mimo że żadne z osobna na to nie pozwala.
        if (!odwiedzone.Add(projekt.Id))
        {
            return;
        }

        rows.Add(new ProjectRow(
            projekt.Id,
            projekt.Outcome,
            depth,
            IsArea: false,
            IsBlocked: blocked?.Contains(projekt.Id) ?? false));

        if (byParent.TryGetValue(projekt.Id, out var dzieci))
        {
            foreach (var dziecko in dzieci)
            {
                Dopisz(dziecko, depth + 1, rows, byParent, odwiedzone, blocked);
            }
        }
    }
}
