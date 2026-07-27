using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Extensions;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

[ApiController]
[Route("api/v1/admin/analytics")]
[Authorize]
public class AdminAnalyticsController : ControllerBase
{
    private readonly IAdminAnalyticsService _adminAnalyticsService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminAnalyticsController> _logger;

    public AdminAnalyticsController(
        IAdminAnalyticsService adminAnalyticsService,
        IConfiguration configuration,
        ILogger<AdminAnalyticsController> logger)
    {
        _adminAnalyticsService = adminAnalyticsService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery] string granularity = "day",
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsAdminRequest())
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Admin permission is required."
                });
            }

            var result = await _adminAnalyticsService.GetOverviewAsync(
                granularity,
                from,
                to,
                cancellationToken);

            return Ok(new ApiResponse<AdminAnalyticsOverviewResponse>
            {
                Status = "success",
                Message = "Admin analytics overview retrieved successfully.",
                Data = result
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting admin analytics overview.");
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred. Please try again."
            });
        }
    }

    [HttpGet("reviews")]
    public async Task<IActionResult> GetReviews(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsAdminRequest())
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Admin permission is required."
                });
            }

            var reviews = await _adminAnalyticsService.GetReviewsAsync(cancellationToken);

            return Ok(new ApiResponse<IReadOnlyList<AdminReviewResponse>>
            {
                Status = "success",
                Message = "Google Play reviews retrieved successfully.",
                Data = reviews
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting Google Play reviews.");
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred. Please try again."
            });
        }
    }

    private bool IsAdminRequest()
    {
        var adminClaim = User.FindFirst("admin")?.Value;
        if (string.Equals(adminClaim, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(adminClaim, "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var email = User.GetFirebaseEmailOrEmpty();
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var configuredEmails = _configuration.GetSection("Admin:Emails").Get<string[]>()
            ?? Array.Empty<string>();

        return configuredEmails.Any(configuredEmail =>
            string.Equals(
                configuredEmail?.Trim(),
                email.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }
}
