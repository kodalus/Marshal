using Marshal.Domain.Areas;
using Marshal.Domain.Projects;

namespace Marshal.Application.UseCases;

/// <summary>Wiersz drzewa: obszar albo projekt, z głębokością zagnieżdżenia.</summary>
public sealed record ProjectRow(
    Guid Id, string Label, int Depth, bool IsArea, bool IsBlocked = false, string? Color = null)
{
    /// <summary>
    /// Obszar wiersza. Dla wiersza obszaru to on sam.
    /// </summary>
    /// <remarks>
    /// Potrzebny przy wybieraniu miejsca dla zadania: wybór podprojektu musi ustawić
    /// **obszar i projekt naraz**, bo zadanie w projekcie zawsze należy do obszaru (N11),
    /// a odczytywanie go z kolejności wierszy byłoby zgadywaniem z układu ekranu.
    /// </remarks>
    public Guid AreaId { get; init; }

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

        var known = projects.Select(p => p.Id).ToHashSet();

        // Jeden zbiór na całe drzewo, nie na gałąź: po scaleniu dwóch urządzeń projekt
        // może trafić pod rodzica z innego obszaru i bez tego pojawiłby się dwa razy.
        var visited = new HashSet<Guid>();

        foreach (var area in areas.OrderBy(a => a.SortOrder))
        {
            rows.Add(new ProjectRow(area.Id, area.Name, 0, IsArea: true, Color: area.Color)
            {
                AreaId = area.Id,
            });

            // Korzeniem w obszarze jest projekt bez rodzica **albo taki, którego rodzic
            // nie dotarł**. Przy synchronizacji plikowej zmiany przychodzą w kolejności
            // zapisu, nie zależności, więc podprojekt potrafi wyprzedzić swój cel.
            // Ukrycie go do czasu przyjścia rodzica oznaczałoby, że zadanie istnieje,
            // ale nie widać go nigdzie.
            var roots = projects
                .Where(p => p.AreaId == area.Id
                         && (p.ParentProjectId is null || !known.Contains(p.ParentProjectId.Value)))
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.Outcome);

            foreach (var project in roots)
            {
                Append(project, depth: 1, rows, byParent, visited, blocked, area.Color);
            }
        }

        return rows;
    }

    private static void Append(
        Project project,
        int depth,
        List<ProjectRow> rows,
        Dictionary<Guid, List<Project>> byParent,
        HashSet<Guid> visited,
        IReadOnlySet<Guid>? blocked,
        string? areaColor)
    {
        // Zabezpieczenie przed cyklem: przy scalaniu dwóch urządzeń da się otrzymać
        // projekt będący własnym przodkiem, mimo że żadne z osobna na to nie pozwala.
        if (!visited.Add(project.Id))
        {
            return;
        }

        // Barwa własna albo odziedziczona — ta sama reguła, którą stosuje kalendarz.
        // Wiersz pokazuje więc kolor, jaki zadania naprawdę dostaną, a nie puste pole
        // przy projekcie, który kolor ma, tyle że po rodzicu.
        var color = string.IsNullOrWhiteSpace(project.Color) ? areaColor : project.Color;

        rows.Add(new ProjectRow(
            project.Id,
            project.Outcome,
            depth,
            IsArea: false,
            IsBlocked: blocked?.Contains(project.Id) ?? false,
            Color: color)
        {
            AreaId = project.AreaId,
        });

        if (byParent.TryGetValue(project.Id, out var children))
        {
            foreach (var child in children)
            {
                Append(child, depth + 1, rows, byParent, visited, blocked, color);
            }
        }
    }
}
