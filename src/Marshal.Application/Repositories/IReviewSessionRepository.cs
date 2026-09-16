using Marshal.Domain.Review;

namespace Marshal.Application.Repositories;

public interface IReviewSessionRepository
{
    /// <summary>Niedokończony przegląd, jeśli jakiś jest. Najnowszy, gdy byłoby kilka.</summary>
    Task<ReviewSession?> OpenAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ReviewSession>> RecentAsync(int limit, CancellationToken ct = default);

    void Add(ReviewSession session);
}
