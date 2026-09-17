using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;

namespace Marshal.Application.UseCases;

/// <summary>
/// Zmiany w szkielecie: obszary i projekty — zakładanie, nazwa, barwa, usunięcie.
/// </summary>
/// <remarks>
/// <para>
/// Jedna usługa na oba poziomy, bo reguły są te same: nazwa, barwa dziedziczona w dół
/// i usunięcie dopuszczalne tylko wtedy, gdy nie ma czego osierocić. Dwie usługi
/// znaczyłyby dwa miejsca do rozjechania się przy pierwszej zmianie którejkolwiek
/// z tych reguł.
/// </para>
/// <para>
/// Barwa jest tu, a nie przy zadaniu, bo ustawia się ją raz na obszarze i schodzi
/// w dół sama. Gdyby kolorować zadania po jednym, kolor przestałby cokolwiek znaczyć
/// po pierwszym tygodniu — a ma odpowiadać na pytanie „czym się dziś zajmowałam",
/// zadane jednym spojrzeniem na siatkę.
/// </para>
/// </remarks>
public sealed class StructureEditService(
    IProjectRepository projects,
    IAreaRepository areas,
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc)
{
    /// <summary>
    /// Nowy obszar. Ląduje na końcu, bo kolejność jest decyzją, a nie alfabetem.
    /// </summary>
    public async Task<Guid> AddAreaAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var istniejace = await areas.AllAsync(ct);
        var kolejnosc = istniejace.Count == 0 ? 0 : istniejace.Max(o => o.SortOrder) + 1;

        var obszar = new Area(Guid.CreateVersion7(), clock.Now, hlc.Next(), name, kolejnosc);
        areas.Add(obszar);
        await unitOfWork.SaveChangesAsync(ct);

        return obszar.Id;
    }

    public async Task RenameAreaAsync(Guid areaId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (await areas.FindAsync(areaId, ct) is not { } obszar)
        {
            return;
        }

        obszar.Rename(name, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Czy obszar da się usunąć: tylko pusty i tylko wtedy, gdy nie jest ostatni.
    /// </summary>
    /// <remarks>
    /// Każde zadanie musi należeć do obszaru (N11), więc usunięcie obszaru z zawartością
    /// nie ma dobrej odpowiedzi — tak samo jak przy projekcie. Liczone są rzeczy żywe:
    /// zadanie wykonane albo wyrzucone zachowuje wskazanie na obszar, ale nikt już
    /// z tego wskazania nie korzysta, więc nie ma powodu blokować nim sprzątania.
    /// Ostatni obszar jest osobno:
    /// bez żadnego nie da się nadać dnia wykonania niczemu, więc jego usunięcie
    /// zablokowałoby aplikację w sposób, po którym nie widać, co się stało.
    /// </remarks>
    public async Task<string?> WhyCannotDeleteAreaAsync(Guid areaId, CancellationToken ct = default)
    {
        if ((await areas.AllAsync(ct)).Count <= 1)
        {
            return "To ostatni obszar — bez żadnego nie da się nadać zadaniu dnia wykonania.";
        }

        if ((await projects.ByAreaAsync(areaId, ct)).Count is var ile and > 0)
        {
            return $"Obszar ma projekty ({ile}) — przenieś je albo usuń najpierw.";
        }

        var zadania = await tasks.ByAreaAsync(areaId, ct);

        return zadania.Count > 0
            ? $"Obszar ma zadania ({zadania.Count}) — przenieś je gdzie indziej."
            : null;
    }

    /// <summary>Usunięcie obszaru. Nagrobkiem, nie kasowaniem.</summary>
    public async Task<string?> DeleteAreaAsync(Guid areaId, CancellationToken ct = default)
    {
        if (await WhyCannotDeleteAreaAsync(areaId, ct) is { } przeszkoda)
        {
            return przeszkoda;
        }

        if (await areas.FindAsync(areaId, ct) is not { } obszar)
        {
            return null;
        }

        obszar.MarkDeleted(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return null;
    }

    /// <summary>
    /// Nowy projekt w obszarze albo pod istniejącym projektem.
    /// </summary>
    /// <remarks>
    /// Podprojekt przejmuje obszar rodzica i nie może go zmienić (N11) — inaczej cel
    /// rozjechałby się po kilku obszarach i przestał być policzalny. Projekt rodzi się
    /// bez następnej akcji, więc od razu zgłasza się jako zablokowany (N1); to jest
    /// prawda o nim, a nie usterka, i lepiej, żeby było ją widać od pierwszej chwili.
    /// </remarks>
    public async Task<Guid?> AddProjectAsync(
        Guid parentId, string outcome, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        var wszystkie = await projects.AllAsync(ct);
        var kolejnosc = wszystkie.Count == 0 ? 0 : wszystkie.Max(p => p.SortOrder) + 1;

        // Rodzicem bywa obszar albo projekt. Jedno wejście na oba, bo z punktu widzenia
        // ręki to ta sama czynność: „tutaj ma powstać nowy".
        if (wszystkie.FirstOrDefault(p => p.Id == parentId) is { } rodzic)
        {
            var pod = new Project(
                Guid.CreateVersion7(), clock.Now, hlc.Next(), outcome,
                rodzic.AreaId, kolejnosc, rodzic.Id);

            projects.Add(pod);
            await unitOfWork.SaveChangesAsync(ct);

            return pod.Id;
        }

        if (await areas.FindAsync(parentId, ct) is null)
        {
            return null;
        }

        var projekt = new Project(
            Guid.CreateVersion7(), clock.Now, hlc.Next(), outcome, parentId, kolejnosc);

        projects.Add(projekt);
        await unitOfWork.SaveChangesAsync(ct);

        return projekt.Id;
    }

    /// <summary>Nowa nazwa projektu. Nazwa projektu jest wynikiem, nie czynnością.</summary>
    public async Task RenameProjectAsync(Guid projectId, string outcome, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        if (await projects.FindAsync(projectId, ct) is not { } projekt)
        {
            return;
        }

        projekt.Rename(outcome, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

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
