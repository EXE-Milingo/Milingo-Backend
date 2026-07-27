namespace Milingo.Backend.Services;

public sealed class GooglePlayReviewSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GooglePlayReviewSyncWorker> _logger;

    public GooglePlayReviewSyncWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<GooglePlayReviewSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredHours = _configuration.GetValue<double>(
            "Google:ReviewSyncIntervalHours",
            24);
        var interval = TimeSpan.FromHours(Math.Clamp(configuredHours, 1, 168));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var analyticsService = scope.ServiceProvider
                    .GetRequiredService<IAdminAnalyticsService>();
                var synced = await analyticsService.SyncGooglePlayReviewsAsync(stoppingToken);

                _logger.LogInformation(
                    "Google Play review synchronization completed. Synced {ReviewCount} reviews. Next run in {IntervalHours} hours.",
                    synced,
                    interval.TotalHours);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled Google Play review synchronization failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
