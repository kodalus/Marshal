using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Tags;

namespace Marshal.Application.UseCases;

public sealed class TagService(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc)
{
    public Task<IReadOnlyList<Tag>> AllAsync(CancellationToken ct = default) => tags.AllAsync(ct);

    public Task<IReadOnlyList<Tag>> ForTaskAsync(Guid taskId, CancellationToken ct = default) =>
        tags.ForTaskAsync(taskId, ct);

    /// <summary>
    /// Przypina tag po nazwie, tworząc go, jeśli jeszcze nie istnieje.
    /// </summary>
    /// <remarks>
    /// Zdjęte powiązanie jest **przywracane**, a nie tworzone od nowa. Nowy rekord
    /// obok istniejącego nagrobka dałby po scaleniu dwa powiązania tego samego
    /// zadania z tym samym tagiem, z których jedno byłoby zdjęte — i wynik zależałby
    /// od kolejności odczytu.
    /// </remarks>
    public async Task<Guid> AttachAsync(Guid taskId, string tagName, CancellationToken ct = default)
    {
        var tag = await tags.FindByNameAsync(tagName, ct);

        if (tag is null)
        {
            tag = new Tag(Guid.CreateVersion7(), clock.Now, hlc.Next(), tagName, sortOrder: 0);
            tags.Add(tag);
        }

        var existing = (await tags.LinksForTaskAsync(taskId, ct))
            .FirstOrDefault(l => l.TagId == tag.Id);

        if (existing is null)
        {
            tags.AddLink(new TaskTag(Guid.CreateVersion7(), clock.Now, hlc.Next(), taskId, tag.Id));
        }
        else if (existing.Deleted)
        {
            existing.Restore(hlc.Next());
        }

        await unitOfWork.SaveChangesAsync(ct);
        return tag.Id;
    }

    public async Task DetachAsync(Guid taskId, Guid tagId, CancellationToken ct = default)
    {
        var link = (await tags.LinksForTaskAsync(taskId, ct)).FirstOrDefault(l => l.TagId == tagId);

        if (link is null || link.Deleted)
        {
            return;
        }

        link.MarkDeleted(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }
}
