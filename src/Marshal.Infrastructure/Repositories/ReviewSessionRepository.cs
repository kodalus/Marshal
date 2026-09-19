using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Review;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class ReviewSessionRepository(MarshalDbContext db, IDbQueue? queue = null)
    : IReviewSessionRepository
{
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    public async Task<ReviewSession?> OpenAsync(CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.ReviewSessions
            .Where(r => r.CompletedAt == null && !r.Deleted)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct), ct);

    public async Task<IReadOnlyList<ReviewSession>> RecentAsync(
        int limit, CancellationToken ct = default) =>
        await _queue.RunAsync(() => db.ReviewSessions
            .Where(r => !r.Deleted)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToListAsync(ct), ct);

    public void Add(ReviewSession session) => db.ReviewSessions.Add(session);
}
