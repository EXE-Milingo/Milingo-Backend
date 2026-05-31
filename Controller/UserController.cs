using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Extensions;
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

    [HttpGet("me")]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid token."
                });

            var profile = await _firestoreService.GetUserProfileAsync(uid, cancellationToken);
            if (profile is null)
                return NotFound(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Profile not found."
                });

            var email = User.GetFirebaseEmailOrEmpty();
            if (string.IsNullOrWhiteSpace(profile.Email) && !string.IsNullOrWhiteSpace(email))
            {
                profile.Email = email;
            }

            return Ok(new ApiResponse<UserProfileResponse>
            {
                Status = "success",
                Message = "User profile retrieved successfully.",
                Data = profile
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting profile for user.");
            return StatusCode(500, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred."
            });
        }
    }

    [HttpPatch("me")]
    public async Task<IActionResult> UpdateProfile(
        [FromBody] UpdateUserProfileRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid token."
                });

            request ??= new UpdateUserProfileRequest();
            if (!ModelState.IsValid)
                return BadRequest(new ApiResponse<object>
                {
                    Status = "error",
                    Message = string.Join(
                        " ",
                        ModelState.Values
                            .SelectMany(value => value.Errors)
                            .Select(error => error.ErrorMessage))
                });

            var profile = await _firestoreService.UpdateUserProfileAsync(
                uid,
                User.GetFirebaseEmailOrEmpty(),
                request,
                cancellationToken);

            return Ok(new ApiResponse<UserProfileResponse>
            {
                Status = "success",
                Message = "User profile updated successfully.",
                Data = profile
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating profile for user.");
            return StatusCode(500, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred."
            });
        }
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
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
            {
                return Unauthorized(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid token: User identifier not found in claims."
                });
            }

            // ─── 2. SECURITY: Extract email from JWT claims ───
            var email = User.GetFirebaseEmailOrEmpty();

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

    /// <summary>
    /// Trả về stats tổng hợp của user hiện tại: coins, streak, totalPoints.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid token."
                });

            var stats = await _firestoreService.GetUserStatsAsync(uid, cancellationToken);

            return Ok(new ApiResponse<UserStatsResponse>
            {
                Status = "success",
                Message = "User stats retrieved successfully.",
                Data = stats
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stats for user.");
            return StatusCode(500, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred."
            });
        }
    }

    /// <summary>
    /// Ghi nhận user học flashcard hôm nay. Idempotent.
    /// Tự động cập nhật streak theo logic: hôm qua học → +1, bỏ ngày → reset 1.
    /// </summary>
    [HttpPost("record-study")]
    public async Task<IActionResult> RecordStudy(CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid token."
                });

            var stats = await _firestoreService.RecordFlashcardStudyAsync(uid, cancellationToken);

            return Ok(new ApiResponse<UserStatsResponse>
            {
                Status = "success",
                Message = "Study session recorded.",
                Data = stats
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording study for user.");
            return StatusCode(500, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred."
            });
        }
    }
}
