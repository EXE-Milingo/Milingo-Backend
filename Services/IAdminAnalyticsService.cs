using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

public interface IAdminAnalyticsService
{
    Task<AdminAnalyticsOverviewResponse> GetOverviewAsync(
        string granularity,
        DateTime? from,
        DateTime? to,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminReviewResponse>> GetReviewsAsync(
        CancellationToken cancellationToken = default);

    Task<int> SyncGooglePlayReviewsAsync(
        CancellationToken cancellationToken = default);
}
