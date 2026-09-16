using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Filters;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>
/// Filtry łączone i zapisane widoki (spec 11.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Warunki sprawdzane w pamięci, nie w zapytaniu.</b> Przetłumaczenie konstruktora
/// warunków na wyrażenie, które EF Core umie zamienić na SQL, znaczyłoby budowanie
/// drzewa wyrażeń z gałęzią na każde pole i każde okno czasowe — czyli najbardziej
/// podatną na błędy część całej aplikacji, przy czym błąd objawia się wyjątkiem
/// dopiero przy uruchomieniu konkretnej kombinacji, więc testy łapią go tylko wtedy,
/// gdy ktoś pomyślał akurat o tej kombinacji.
/// </para>
/// <para>
/// Po drugiej stronie wagi stoi baza jednej osoby: kilka tysięcy zadań to rząd
/// wielkości jednego odczytu z dysku. Przy stu tysiącach decyzja byłaby inna;
/// tu różnicy nie widać, a sprawdzanie warunków jest czystą funkcją, którą da się
/// przetestować w całości — zob. <see cref="FilterQuery.Matches"/>.
/// </para>
/// </remarks>
public sealed class FilterService(
    ISavedFilterRepository filters,
    ITaskRepository tasks,
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc)
{
    public async Task<IReadOnlyList<TaskItem>> RunAsync(
        FilterQuery? query, CancellationToken ct = default)
    {
        if (query is null || query.IsEmpty)
        {
            return [];
        }

        var wszystkie = await tasks.AllAsync(ct);

        // Powiązania z tagami wczytane raz i pogrupowane, zamiast pytania o tagi przy
        // każdym zadaniu. Przy tysiącu zadań to różnica między jednym odczytem
        // a tysiącem — i to niezależnie od tego, czy filtr w ogóle pyta o tagi.
        var poZadaniu = (await tags.AllLinksAsync(ct))
            .GroupBy(l => l.TaskId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<Guid>)g.Select(l => l.TagId).ToArray());

        var dzis = clock.Today;

        return query
            .Apply(
                wszystkie.Select(z => new FilterSubject(
                    z, poZadaniu.TryGetValue(z.Id, out var t) ? t : [])),
                dzis)
            .ToArray();
    }

    public Task<IReadOnlyList<SavedFilter>> FavouritesAsync(CancellationToken ct = default) =>
        filters.AllAsync(ct);

    public Task<SavedFilter?> FindAsync(Guid id, CancellationToken ct = default) =>
        filters.FindAsync(id, ct);

    /// <summary>Zapisanie ułożonego filtra do Ulubionych, na koniec listy.</summary>
    public async Task<SavedFilter> SaveAsync(
        string name, FilterQuery query, CancellationToken ct = default)
    {
        var filtr = SavedFilter.Create(name, query, clock.Now, hlc.Next());

        var ostatni = (await filters.AllAsync(ct)).LastOrDefault();
        filtr.SetSortOrder((ostatni?.SortOrder ?? 0) + 1, hlc.Next());

        filters.Add(filtr);
        await unitOfWork.SaveChangesAsync(ct);

        return filtr;
    }

    public async Task<SavedFilter?> UpdateAsync(
        Guid id, string name, FilterQuery query, CancellationToken ct = default)
    {
        if (await filters.FindAsync(id, ct) is not { } filtr)
        {
            return null;
        }

        // Każde pole ruszane tylko wtedy, gdy się zmieniło — zob. TaskEditService.
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != filtr.Name)
        {
            filtr.Rename(name, hlc.Next());
        }

        if (!query.IsEmpty && query.ToJson() != filtr.DefinitionJson)
        {
            filtr.SetQuery(query, hlc.Next());
        }

        await unitOfWork.SaveChangesAsync(ct);
        return filtr;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (await filters.FindAsync(id, ct) is not { } filtr)
        {
            return;
        }

        // Nagrobek, nie usunięcie fizyczne (spec 5.1).
        filtr.MarkDeleted(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }
}
