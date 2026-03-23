using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize] // All endpoints require a valid Firebase JWT
public class UserController : ControllerBase
{
    private readonly IFirestoreService _firestoreService;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IFirestoreService firestoreService,
        ILogger<UserController> logger)
    {
        _firestoreService = firestoreService;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a new user profile in Firestore after Firebase Auth registration.
    /// Idempotent — if the profile already exists, it will NOT be overwritten.
    /// </summary>
    [HttpPost("init-profile")]
    public async Task<IActionResult> InitProfile(
        [FromBody] InitProfileRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // ─── 1. SECURITY: Extract UID from verified Firebase JWT ───
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid))
            {
                return Unauthorized(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid token: User identifier not found in claims."
                });
            }

            // ─── 2. SECURITY: Extract email from JWT claims ───
            var email = User.FindFirstValue(ClaimTypes.Email)
                        ?? User.FindFirstValue("email")
                        ?? string.Empty;

            _logger.LogInformation(
                "Initializing profile for user '{Uid}' ({Email}).", uid, email);

            // ─── 3. FIRESTORE: Create profile (idempotent) ───
            var created = await _firestoreService.InitUserProfileAsync(
                uid,
                email,
                request.DisplayName,
                request.TargetLanguage,
                cancellationToken);

            // ─── 4. RESPONSE ───
            if (!created)
            {
                _logger.LogInformation(
                    "Profile already exists for user '{Uid}'. No changes made.", uid);

                return Ok(new ApiResponse<object>
                {
                    Status = "success",
                    Message = "Profile already exists. No changes were made."
                });
            }

            _logger.LogInformation(
                "Profile created successfully for user '{Uid}'. +50 welcome coins!", uid);

            return StatusCode(StatusCodes.Status201Created, new ApiResponse<object>
            {
                Status = "success",
                Message = "Profile created successfully. Welcome to Milingo! +50 coins!"
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Request cancelled — client disconnected.");
            return StatusCode(499); // 499 Client Closed Request (nginx convention)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during profile initialization for user.");
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred. Please try again."
            });
        }
    }

    /// <summary>
    /// Returns the list of supported target languages for the language picker UI.
    /// This endpoint is public — no authentication required.
    /// </summary>
    [HttpGet("supported-languages")]
    [AllowAnonymous]
    public IActionResult GetSupportedLanguages()
    {
        return Ok(new ApiResponse<IReadOnlyList<LanguageInfo>>
        {
            Status = "success",
            Message = "Supported languages retrieved successfully.",
            Data = SupportedLanguages.Details
        });
    }
}
