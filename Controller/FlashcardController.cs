using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Extensions;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

[ApiController]
[Route("api/v1/flashcards")]
[Authorize]
public class FlashcardController : ControllerBase
{
    private readonly IFirestoreService _firestoreService;
    private readonly ILogger<FlashcardController> _logger;

    public FlashcardController(
        IFirestoreService firestoreService,
        ILogger<FlashcardController> logger)
    {
        _firestoreService = firestoreService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/v1/flashcards/saved-status?term=apple&amp;sourceLangCode=en&amp;targetLangCode=vi
    /// Checks whether a term has already been saved in any deck of the current user.
    /// </summary>
    [HttpGet("saved-status")]
    public async Task<IActionResult> GetSavedStatus(
        [FromQuery] string term,
        [FromQuery] string sourceLangCode,
        [FromQuery] string targetLangCode,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new ApiResponse<object>
            {
                Status = "error",
                Message = "Invalid token: User identifier not found in claims."
            });
        }

        if (string.IsNullOrWhiteSpace(term))
        {
            return BadRequest(new ApiResponse<object>
            {
                Status = "error",
                Message = "Query parameter 'term' is required."
            });
        }

        if (string.IsNullOrWhiteSpace(sourceLangCode))
        {
            return BadRequest(new ApiResponse<object>
            {
                Status = "error",
                Message = "Query parameter 'sourceLangCode' is required."
            });
        }

        if (string.IsNullOrWhiteSpace(targetLangCode))
        {
            return BadRequest(new ApiResponse<object>
            {
                Status = "error",
                Message = "Query parameter 'targetLangCode' is required."
            });
        }

        try
        {
            var result = await _firestoreService.GetSavedStatusAsync(
                userId, term, sourceLangCode, targetLangCode, cancellationToken);

            return Ok(new ApiResponse<SavedStatusResponse>
            {
                Status = "success",
                Message = result.IsSaved
                    ? $"Term found in {result.DeckIds.Count} deck(s)."
                    : "Term not found in any deck.",
                Data = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check saved status for user '{UserId}'.", userId);
            return StatusCode(500, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred while checking saved status."
            });
        }
    }
}
