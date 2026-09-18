using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Tags;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class TagRepository(MarshalDbContext db, IKolejkaBazy? kolejka = null)
    : ITagRepository
{
    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    public async Task<IReadOnlyList<Tag>> AllAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tags.Where(t => !t.Deleted).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct), ct);

    public async Task<Tag?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Tags.FirstOrDefaultAsync(t => t.Id == id && !t.Deleted, ct), ct);

    public async Task<Tag?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var szukane = name.Trim().TrimStart('#');

        // Porównanie bez rozróżniania wielkości liter robione po stronie klienta:
        // SQLite nie zna liter spoza zakresu ASCII, więc „Żłobek" i „żłobek" byłyby
        // dla niego różne. Tagów są dziesiątki, nie tysiące — koszt bez znaczenia.
        var wszystkie = await _kolejka.WykonajAsync(
            () => db.Tags.Where(t => !t.Deleted).ToListAsync(ct), ct);
        return wszystkie.FirstOrDefault(t => Tag.SameName(t.Name, szukane));
    }

    public async Task<IReadOnlyList<TaskTag>> LinksForTaskAsync(Guid taskId, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.TaskTags.Where(l => l.TaskId == taskId).ToListAsync(ct), ct);

    public async Task<IReadOnlyList<TaskTag>> AllLinksAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.TaskTags.Where(l => !l.Deleted).ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Tag>> ForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        // Dwa zapytania pod jednym wejściem do bramy, a nie dwa wejścia: między nimi
        // nie ma nic do zrobienia, a rozdzielone wpuszczałyby w szczelinę cudzy zapis.
        return await _kolejka.WykonajAsync<IReadOnlyList<Tag>>(async () =>
        {
            var tagIds = await db.TaskTags
                .Where(l => l.TaskId == taskId && !l.Deleted)
                .Select(l => l.TagId)
                .ToListAsync(ct);

            return await db.Tags
                .Where(t => tagIds.Contains(t.Id) && !t.Deleted)
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Name)
                .ToListAsync(ct);
        }, ct);
    }

    public void Add(Tag tag) => db.Tags.Add(tag);

    public void AddLink(TaskTag link) => db.TaskTags.Add(link);
}
