using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Extensions;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

[ApiController]
[Route("api/v1/decks")]
[Authorize]
public class DeckController : ControllerBase
{
    private readonly IFirestoreService _firestoreService;
    private readonly ILogger<DeckController> _logger;

    public DeckController(
        IFirestoreService firestoreService,
        ILogger<DeckController> logger)
    {
        _firestoreService = firestoreService;
        _logger = logger;
    }

    // =================================================================
    //  DECK ENDPOINTS
    // =================================================================

    /// <summary>
    /// GET /api/v1/decks
    /// Returns all flashcard decks for the current user, ordered by created_at ascending.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetDecks(CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        try
        {
            var decks = await _firestoreService.GetDecksAsync(userId, cancellationToken);

            return Ok(new ApiResponse<List<DeckResponse>>
            {
                Status = "success",
                Message = $"Retrieved {decks.Count} deck(s).",
                Data = decks
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve decks for user '{UserId}'.", userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while retrieving decks."));
        }
    }

    /// <summary>
    /// POST /api/v1/decks
    /// Creates a new flashcard deck.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateDeck(
        [FromBody] CreateDeckRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        if (!ModelState.IsValid)
            return BadRequest(ErrorResponse(ModelState));

        try
        {
            var deck = await _firestoreService.CreateDeckAsync(userId, request, cancellationToken);

            return StatusCode(StatusCodes.Status201Created, new ApiResponse<DeckResponse>
            {
                Status = "success",
                Message = "Deck created successfully.",
                Data = deck
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create deck for user '{UserId}'.", userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while creating the deck."));
        }
    }

    /// <summary>
    /// PATCH /api/v1/decks/{deckId}
    /// Updates name, description, and/or emoji. Cannot update is_default or vocab_count.
    /// </summary>
    [HttpPatch("{deckId}")]
    public async Task<IActionResult> UpdateDeck(
        string deckId,
        [FromBody] UpdateDeckRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        if (!ModelState.IsValid)
            return BadRequest(ErrorResponse(ModelState));

        // At least one field must be provided
        if (request.Name is null && request.Description is null && request.Emoji is null)
        {
            return BadRequest(ErrorResponse("At least one field (name, description, emoji) must be provided."));
        }

        try
        {
            var deck = await _firestoreService.UpdateDeckAsync(userId, deckId, request, cancellationToken);

            if (deck is null)
                return NotFound(ErrorResponse($"Deck '{deckId}' not found."));

            return Ok(new ApiResponse<DeckResponse>
            {
                Status = "success",
                Message = "Deck updated successfully.",
                Data = deck
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update deck '{DeckId}' for user '{UserId}'.", deckId, userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while updating the deck."));
        }
    }

    /// <summary>
    /// PATCH /api/v1/decks/{deckId}/favorite
    /// Marks or unmarks a deck as favorite.
    /// </summary>
    [HttpPatch("{deckId}/favorite")]
    public async Task<IActionResult> SetDeckFavorite(
        string deckId,
        [FromBody] FavoriteRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        try
        {
            var deck = await _firestoreService.SetDeckFavoriteAsync(
                userId, deckId, request.IsFavorite, cancellationToken);

            if (deck is null)
                return NotFound(ErrorResponse($"Deck '{deckId}' not found."));

            return Ok(new ApiResponse<DeckResponse>
            {
                Status = "success",
                Message = "Deck favorite status updated successfully.",
                Data = deck
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update favorite status for deck '{DeckId}' and user '{UserId}'.",
                deckId, userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while updating the deck favorite status."));
        }
    }

    /// <summary>
    /// DELETE /api/v1/decks/{deckId}
    /// Deletes a deck and all its cards. Cannot delete default decks.
    /// </summary>
    [HttpDelete("{deckId}")]
    public async Task<IActionResult> DeleteDeck(
        string deckId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        try
        {
            var (success, errorReason) = await _firestoreService.DeleteDeckAsync(
                userId, deckId, cancellationToken);

            if (!success)
            {
                // Distinguish between "not found" and "is default"
                if (errorReason?.Contains("default", StringComparison.OrdinalIgnoreCase) == true)
                    return BadRequest(ErrorResponse(errorReason));

                return NotFound(ErrorResponse(errorReason ?? "Deck not found."));
            }

            return Ok(new ApiResponse<object>
            {
                Status = "success",
                Message = "Deck and all its cards deleted successfully."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete deck '{DeckId}' for user '{UserId}'.", deckId, userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while deleting the deck."));
        }
    }

    // =================================================================
    //  CARD ENDPOINTS (nested under /api/v1/decks/{deckId}/cards)
    // =================================================================

    /// <summary>
    /// GET /api/v1/decks/{deckId}/cards
    /// Returns all cards in a deck, ordered by created_at descending.
    /// </summary>
    [HttpGet("{deckId}/cards")]
    public async Task<IActionResult> GetCards(
        string deckId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        try
        {
            var cards = await _firestoreService.GetCardsAsync(userId, deckId, cancellationToken);

            if (cards is null)
                return NotFound(ErrorResponse($"Deck '{deckId}' not found."));

            return Ok(new ApiResponse<List<CardResponse>>
            {
                Status = "success",
                Message = $"Retrieved {cards.Count} card(s).",
                Data = cards
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve cards for deck '{DeckId}', user '{UserId}'.", deckId, userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while retrieving cards."));
        }
    }

    /// <summary>
    /// POST /api/v1/decks/{deckId}/cards
    /// Adds a card to a deck. Checks for duplicates by normalized_term + lang codes.
    /// Returns 409 Conflict if duplicate.
    /// </summary>
    [HttpPost("{deckId}/cards")]
    public async Task<IActionResult> AddCard(
        string deckId,
        [FromBody] AddCardRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        if (!ModelState.IsValid)
            return BadRequest(ErrorResponse(ModelState));

        try
        {
            var (card, conflictMessage, deckNotFound) = await _firestoreService.AddCardAsync(
                userId, deckId, request, cancellationToken);

            if (deckNotFound)
                return NotFound(ErrorResponse($"Deck '{deckId}' not found."));

            if (conflictMessage is not null)
            {
                return Conflict(new ApiResponse<object>
                {
                    Status = "error",
                    Message = conflictMessage
                });
            }

            return StatusCode(StatusCodes.Status201Created, new ApiResponse<CardResponse>
            {
                Status = "success",
                Message = "Card added successfully.",
                Data = card
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add card to deck '{DeckId}' for user '{UserId}'.", deckId, userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while adding the card."));
        }
    }

    /// <summary>
    /// PATCH /api/v1/decks/{deckId}/cards/{cardId}/favorite
    /// Marks or unmarks a card as favorite.
    /// </summary>
    [HttpPatch("{deckId}/cards/{cardId}/favorite")]
    public async Task<IActionResult> SetCardFavorite(
        string deckId,
        string cardId,
        [FromBody] FavoriteRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        try
        {
            var card = await _firestoreService.SetCardFavoriteAsync(
                userId, deckId, cardId, request.IsFavorite, cancellationToken);

            if (card is null)
                return NotFound(ErrorResponse($"Card '{cardId}' not found."));

            return Ok(new ApiResponse<CardResponse>
            {
                Status = "success",
                Message = "Card favorite status updated successfully.",
                Data = card
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update favorite status for card '{CardId}' in deck '{DeckId}' and user '{UserId}'.",
                cardId, deckId, userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while updating the card favorite status."));
        }
    }

    /// <summary>
    /// DELETE /api/v1/decks/{deckId}/cards/{cardId}
    /// Deletes a card and decrements deck.vocab_count.
    /// </summary>
    [HttpDelete("{deckId}/cards/{cardId}")]
    public async Task<IActionResult> DeleteCard(
        string deckId,
        string cardId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

        try
        {
            var (success, errorReason) = await _firestoreService.DeleteCardAsync(
                userId, deckId, cardId, cancellationToken);

            if (!success)
                return NotFound(ErrorResponse(errorReason ?? "Card not found."));

            return Ok(new ApiResponse<object>
            {
                Status = "success",
                Message = "Card deleted successfully."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete card '{CardId}' from deck '{DeckId}' for user '{UserId}'.",
                cardId, deckId, userId);
            return StatusCode(500, ErrorResponse("An unexpected error occurred while deleting the card."));
        }
    }

    // =================================================================
    //  PRIVATE HELPERS
    // =================================================================

    private static ApiResponse<object> ErrorResponse(string message)
    {
        return new ApiResponse<object> { Status = "error", Message = message };
    }

    private static ApiResponse<object> ErrorResponse(Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState)
    {
        var errors = modelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .ToList();

        return new ApiResponse<object>
        {
            Status = "error",
            Message = string.Join(" ", errors)
        };
    }
}
