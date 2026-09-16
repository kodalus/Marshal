using Marshal.Application.Repositories;
using Marshal.Domain.Review;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class ReviewSessionRepository(MarshalDbContext db) : IReviewSessionRepository
{
    public async Task<ReviewSession?> OpenAsync(CancellationToken ct = default) =>
        await db.ReviewSessions
            .Where(r => r.CompletedAt == null && !r.Deleted)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ReviewSession>> RecentAsync(
        int limit, CancellationToken ct = default) =>
        await db.ReviewSessions
            .Where(r => !r.Deleted)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToListAsync(ct);

    public void Add(ReviewSession session) => db.ReviewSessions.Add(session);
}
