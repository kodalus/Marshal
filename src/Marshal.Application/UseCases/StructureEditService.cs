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

        var existing = await areas.AllAsync(ct);
        var order = existing.Count == 0 ? 0 : existing.Max(o => o.SortOrder) + 1;

        var area = new Area(Guid.CreateVersion7(), clock.Now, hlc.Next(), name, order);
        areas.Add(area);
        await unitOfWork.SaveChangesAsync(ct);

        return area.Id;
    }

    public async Task RenameAreaAsync(Guid areaId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (await areas.FindAsync(areaId, ct) is not { } area)
        {
            return;
        }

        area.Rename(name, hlc.Next());
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

        if ((await projects.ByAreaAsync(areaId, ct)).Count is var count and > 0)
        {
            return $"Obszar ma projekty ({count}) — przenieś je albo usuń najpierw.";
        }

        var found = await tasks.ByAreaAsync(areaId, ct);

        return found.Count > 0
            ? $"Obszar ma zadania ({found.Count}) — przenieś je gdzie indziej."
            : null;
    }

    /// <summary>Usunięcie obszaru. Nagrobkiem, nie kasowaniem.</summary>
    public async Task<string?> DeleteAreaAsync(Guid areaId, CancellationToken ct = default)
    {
        if (await WhyCannotDeleteAreaAsync(areaId, ct) is { } blocker)
        {
            return blocker;
        }

        if (await areas.FindAsync(areaId, ct) is not { } area)
        {
            return null;
        }

        area.MarkDeleted(hlc.Next());
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

        var all = await projects.AllAsync(ct);
        var order = all.Count == 0 ? 0 : all.Max(p => p.SortOrder) + 1;

        // Rodzicem bywa obszar albo projekt. Jedno wejście na oba, bo z punktu widzenia
        // ręki to ta sama czynność: „tutaj ma powstać nowy".
        if (all.FirstOrDefault(p => p.Id == parentId) is { } parent)
        {
            var pod = new Project(
                Guid.CreateVersion7(), clock.Now, hlc.Next(), outcome,
                parent.AreaId, order, parent.Id);

            projects.Add(pod);
            await unitOfWork.SaveChangesAsync(ct);

            return pod.Id;
        }

        if (await areas.FindAsync(parentId, ct) is null)
        {
            return null;
        }

        var project = new Project(
            Guid.CreateVersion7(), clock.Now, hlc.Next(), outcome, parentId, order);

        projects.Add(project);
        await unitOfWork.SaveChangesAsync(ct);

        return project.Id;
    }

    /// <summary>Nowa nazwa projektu. Nazwa projektu jest wynikiem, nie czynnością.</summary>
    public async Task RenameProjectAsync(Guid projectId, string outcome, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        if (await projects.FindAsync(projectId, ct) is not { } project)
        {
            return;
        }

        project.Rename(outcome, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task SetAreaColorAsync(Guid areaId, string? color, CancellationToken ct = default)
    {
        if (await areas.FindAsync(areaId, ct) is not { } area)
        {
            return;
        }

        area.SetColor(color, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Przypisanie kalendarza Google do obszaru. Jeden kalendarz na jeden obszar.
    /// </summary>
    /// <remarks>
    /// Zdjęcie przypisania z każdego innego obszaru, który wskazywał ten sam kalendarz,
    /// robione tutaj, a nie zostawiane użytkowniczce. Dwa obszary na jednym kalendarzu
    /// znaczyłyby, że wydarzenie należy do obu — a obszar, który nie dzieli, nie jest
    /// obszarem. Ciche przełożenie jest tu lepsze od odmowy: „ten kalendarz jest już
    /// zajęty przez Pracę" zmuszałoby do pójścia tam, zdjęcia i powrotu, żeby zrobić
    /// dokładnie to, co się przed chwilą wybrało.
    /// </remarks>
    public async Task SetAreaCalendarAsync(
        Guid areaId, Guid? calendarId, CancellationToken ct = default)
    {
        if (await areas.FindAsync(areaId, ct) is not { } area)
        {
            return;
        }

        if (calendarId is { } chosen)
        {
            foreach (var other in (await areas.AllAsync(ct))
                .Where(o => o.Id != areaId && o.CalendarId == chosen))
            {
                other.SetCalendar(null, hlc.Next());
            }
        }

        area.SetCalendar(calendarId, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task SetProjectColorAsync(Guid projectId, string? color, CancellationToken ct = default)
    {
        if (await projects.FindAsync(projectId, ct) is not { } project)
        {
            return;
        }

        project.SetColor(color, hlc.Next());
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
        var all = await projects.AllAsync(ct);

        if (all.Any(p => p.ParentProjectId == projectId))
        {
            return "Projekt ma podprojekty — najpierw usuń albo odepnij je.";
        }

        var found = await tasks.ByProjectAsync(projectId, ct);

        return found.Count > 0
            ? $"Projekt ma zadania ({found.Count}) — przenieś je albo zamknij projekt."
            : null;
    }

    /// <summary>Usunięcie projektu. Nagrobkiem, nie kasowaniem — inaczej wróciłby przy scaleniu.</summary>
    public async Task<string?> DeleteProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        if (await WhyCannotDeleteAsync(projectId, ct) is { } blocker)
        {
            return blocker;
        }

        if (await projects.FindAsync(projectId, ct) is not { } project)
        {
            return null;
        }

        project.MarkDeleted(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return null;
    }
}
