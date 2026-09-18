using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Review;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class ReviewSessionRepository(MarshalDbContext db, IKolejkaBazy? kolejka = null)
    : IReviewSessionRepository
{
    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    public async Task<ReviewSession?> OpenAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.ReviewSessions
            .Where(r => r.CompletedAt == null && !r.Deleted)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct), ct);

    public async Task<IReadOnlyList<ReviewSession>> RecentAsync(
        int limit, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.ReviewSessions
            .Where(r => !r.Deleted)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToListAsync(ct), ct);

    public void Add(ReviewSession session) => db.ReviewSessions.Add(session);
}
