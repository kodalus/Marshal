using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.Repositories;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>
/// Zbieranie i przetwarzanie skrzynki — drzewko decyzyjne z rozdziału 7 spec.
/// </summary>
/// <remarks>
/// Każda gałąź drzewka ma tu dokładnie jedną metodę. Celowo nie ma metody „przetwórz",
/// która przyjmowałaby obiekt decyzji: wybór gałęzi jest wyborem użytkownika przy
/// jednej pozycji na ekranie, a nie danymi do przekazania.
/// </remarks>
public sealed class InboxService(
    ITaskRepository tasks,
    IProjectRepository projects,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc,
    ITaskMirror mirror)
{
    /// <summary>
    /// Wrzut. Jedyne pole to tytuł — zasada 1.3. Ma kosztować dwie sekundy.
    /// </summary>
    public async Task<Guid> CaptureAsync(string title, CancellationToken ct = default)
    {
        var task = TaskItem.Capture(title, clock.Now, hlc.Next());
        tasks.Add(task);
        await unitOfWork.SaveChangesAsync(ct);
        return task.Id;
    }

    public Task<IReadOnlyList<TaskItem>> ListAsync(CancellationToken ct = default) =>
        tasks.InboxAsync(ct);

    public Task<int> CountAsync(CancellationToken ct = default) =>
        tasks.InboxCountAsync(ct);

    /// <summary>
    /// Nie wymaga działania → kosz.
    /// </summary>
    /// <remarks>
    /// Udostępnione zadanie zabiera ze sobą swoje wydarzenie. Zostawione w cudzym
    /// kalendarzu byłoby zaproszeniem na coś, co po tej stronie już nie istnieje —
    /// i nikt by go stamtąd nie zdjął, bo nie miałby po czym poznać, że trzeba.
    /// </remarks>
    public async Task TrashAsync(Guid id, CancellationToken ct = default)
    {
        var task = await Required(id, ct);

        await mirror.RemoveAsync(task, ct);

        task.Trash(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>Nie wymaga działania teraz → kiedyś-może.</summary>
    public Task PostponeAsync(Guid id, Guid areaId, DateOnly? deferUntil, CancellationToken ct = default) =>
        MutateAsync(id, task => task.Postpone(areaId, deferUntil, hlc.Next()), ct);

    /// <summary>Krótsze niż dwie minuty → zrobione na miejscu.</summary>
    public Task DoNowAsync(Guid id, Guid areaId, CancellationToken ct = default) =>
        MutateAsync(id, task =>
        {
            task.MakeNext(areaId, hlc.Next());
            task.Complete(clock.Now, hlc.Next());
        }, ct);

    /// <summary>Nie moja → oczekiwane.</summary>
    public Task DelegateAsync(
        Guid id, Guid areaId, string who, int? nudgeDays = null, CancellationToken ct = default) =>
        MutateAsync(id, task => task.Delegate(
            areaId, who, clock.Today, nudgeDays, hlc.Next()), ct);

    /// <summary>Moja, bez wyznaczonego dnia → następna akcja.</summary>
    public Task MakeNextAsync(Guid id, Guid areaId, Guid? projectId = null, CancellationToken ct = default) =>
        MutateAsync(id, task =>
        {
            task.MakeNext(areaId, hlc.Next());
            if (projectId is not null)
            {
                task.MoveTo(areaId, projectId, hlc.Next());
            }
        }, ct);

    /// <summary>Musi być w konkretnym dniu → zaplanowane.</summary>
    public Task ScheduleAsync(Guid id, Guid areaId, DateOnly doDate, CancellationToken ct = default) =>
        MutateAsync(id, task => task.Schedule(areaId, doDate, hlc.Next()), ct);

    /// <summary>
    /// Więcej niż jeden krok → nowy projekt, a ta pozycja zostaje jego pierwszą
    /// następną akcją. Projekt powstaje od razu z akcją, żeby nie urodził się
    /// zablokowany (N1).
    /// </summary>
    public async Task<Guid> PromoteToProjectAsync(
        Guid id, Guid areaId, string outcome, string firstActionTitle, CancellationToken ct = default)
    {
        var task = await Required(id, ct);

        var project = new Project(
            Guid.CreateVersion7(), clock.Now, hlc.Next(), outcome, areaId, sortOrder: 0);
        projects.Add(project);

        task.Rename(firstActionTitle, hlc.Next());
        task.MakeNext(areaId, hlc.Next());
        task.MoveTo(areaId, project.Id, hlc.Next());

        await unitOfWork.SaveChangesAsync(ct);
        return project.Id;
    }

    private async Task MutateAsync(Guid id, Action<TaskItem> change, CancellationToken ct)
    {
        var task = await Required(id, ct);
        change(task);
        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task<TaskItem> Required(Guid id, CancellationToken ct) =>
        await tasks.FindAsync(id, ct)
        ?? throw new InvalidOperationException($"Nie ma zadania o identyfikatorze {id}.");
}
