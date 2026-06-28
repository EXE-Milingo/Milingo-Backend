using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Milingo.Backend.Extensions;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

[ApiController]
[Route("api/v1/study")]
[Authorize]
public class StudyController : ControllerBase
{
    private readonly IFirestoreService _firestoreService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StudyController> _logger;

    public StudyController(
        IFirestoreService firestoreService,
        IServiceProvider serviceProvider,
        ILogger<StudyController> logger)
    {
        _firestoreService = firestoreService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    [HttpGet("session")]
    public async Task<IActionResult> GetStudySession(
        [FromQuery] string? deckId,
        [FromQuery] int limit = 20,
        [FromQuery] string? targetLanguage = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(Err("Invalid token."));

        limit = Math.Clamp(limit, 1, 50);

        try
        {
            var allDecks = string.IsNullOrWhiteSpace(deckId)
                || string.Equals(deckId, "all", StringComparison.OrdinalIgnoreCase);
            var requestedDeckId = deckId?.Trim() ?? string.Empty;

            string? targetLang = targetLanguage;
            if (string.IsNullOrWhiteSpace(targetLang))
            {
                var profile = await _firestoreService.GetUserProfileAsync(userId, cancellationToken);
                targetLang = profile?.TargetLanguage;
            }

            var normalizedLang = string.IsNullOrWhiteSpace(targetLang)
                ? null
                : SupportedLanguages.NormalizeLanguageCode(targetLang);

            await _firestoreService.FixIncorrectCardsNextReviewTimeAsync(userId, cancellationToken);

            var dueCards = allDecks
                ? await _firestoreService.GetAllDueCardsAsync(userId, limit, normalizedLang, cancellationToken)
                : await _firestoreService.GetDueCardsAsync(userId, requestedDeckId, limit, normalizedLang, cancellationToken);

            if (dueCards.Count == 0)
            {
                return Ok(new ApiResponse<StudySessionResponse>
                {
                    Status = "success",
                    Message = "No cards due right now. Great work!",
                    Data = new StudySessionResponse
                    {
                        DeckId = allDecks ? string.Empty : requestedDeckId,
                        DeckName = allDecks ? "All decks" : string.Empty,
                        TotalDue = 0
                    }
                });
            }

            var studyCards = await BuildStudyCardsAsync(userId, dueCards, cancellationToken);
            var flashcardCount = studyCards.Count(c => c.SuggestedMode == "flashcard");
            var mcqCount = studyCards.Count(c => c.SuggestedMode == "mcq");

            var responseDeckId = allDecks ? string.Empty : requestedDeckId;
            var responseDeckName = allDecks
                ? "All decks"
                : dueCards.FirstOrDefault()?.DeckName ?? string.Empty;

            return Ok(new ApiResponse<StudySessionResponse>
            {
                Status = "success",
                Message = allDecks
                    ? $"{studyCards.Count} card(s) ready across all decks."
                    : $"{studyCards.Count} card(s) ready.",
                Data = new StudySessionResponse
                {
                    DeckId = responseDeckId,
                    DeckName = responseDeckName,
                    Cards = studyCards,
                    TotalDue = dueCards.Count,
                    FlashcardCount = flashcardCount,
                    McqCount = mcqCount
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStudySession failed for user {UserId}, deck {DeckId}.", userId, deckId ?? "all");
            return StatusCode(500, Err("An unexpected error occurred."));
        }
    }

    [HttpGet("daily-session")]
    public async Task<IActionResult> GetDailySession(
        [FromQuery] int limit = 30,
        [FromQuery] string? targetLanguage = null,
        CancellationToken cancellationToken = default)
    {
        return await GetStudySession("all", limit, targetLanguage, cancellationToken);
    }

    [HttpPost("answer")]
    public async Task<IActionResult> SubmitAnswer(
        [FromBody] SubmitStudyAnswerRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(Err("Invalid token."));

        if (string.IsNullOrWhiteSpace(request.CardId) || string.IsNullOrWhiteSpace(request.DeckId))
            return BadRequest(Err("card_id and deck_id are required."));

        if (request.Mode != "flashcard" && request.Mode != "mcq")
            return BadRequest(Err("mode must be 'flashcard' or 'mcq'."));

        int quality;
        if (request.Mode == "mcq")
        {
            if (request.IsCorrect is null)
                return BadRequest(Err("is_correct is required for MCQ mode."));

            quality = request.IsCorrect.Value ? 4 : 1;
        }
        else
        {
            if (request.Quality is null)
                return BadRequest(Err("quality is required for flashcard mode."));

            if (request.Quality is not (0 or 2 or 4 or 5))
                return BadRequest(Err("quality must be 0 (Again), 2 (Hard), 4 (Good), or 5 (Easy)."));

            quality = request.Quality.Value;
        }

        try
        {
            var result = await _firestoreService.UpdateCardSrsAsync(
                userId, request.DeckId, request.CardId, quality, request.Mode, cancellationToken);
            if (string.IsNullOrEmpty(result.NewSrsState))
                return NotFound(Err("Card not found."));

            var message = request.Mode == "mcq"
                ? quality >= 3 ? "Correct! Scheduled for later." : "Not quite. This one will come back soon."
                : quality switch
                {
                    0 => "Got it. We'll show this one again shortly.",
                    2 => "Hard one. See you in a day.",
                    4 => "Good. See you in a few days.",
                    5 => "Easy. See you much later!",
                    _ => "Answer recorded."
                };

            return Ok(new ApiResponse<SubmitStudyAnswerResponse>
            {
                Status = "success",
                Message = message,
                Data = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubmitAnswer failed for user {UserId}, card {CardId}.", userId, request.CardId);
            return StatusCode(500, Err("An unexpected error occurred."));
        }
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStudyStats(
        [FromQuery] string deckId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(Err("Invalid token."));

        if (string.IsNullOrWhiteSpace(deckId))
            return BadRequest(Err("deckId is required."));

        try
        {
            var stats = await _firestoreService.GetDeckStudyStatsAsync(userId, deckId, cancellationToken);

            return Ok(new ApiResponse<DeckStudyStats>
            {
                Status = "success",
                Message = "Deck study stats retrieved.",
                Data = stats
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStudyStats failed for user {UserId}, deck {DeckId}.", userId, deckId);
            return StatusCode(500, Err("An unexpected error occurred."));
        }
    }

    [HttpPost("migrate-srs")]
    public async Task<IActionResult> MigrateSrsFields(CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(Err("Invalid token."));

        try
        {
            var updated = await _firestoreService.MigrateSrsFieldsAsync(userId, cancellationToken);

            return Ok(new ApiResponse<object>
            {
                Status = "success",
                Message = $"SRS migration complete. {updated} card(s) updated.",
                Data = new { cards_migrated = updated }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SRS migration failed for user {UserId}.", userId);
            return StatusCode(500, Err("Migration failed. Check server logs."));
        }
    }

    private async Task<List<StudyCard>> BuildStudyCardsAsync(
        string userId,
        List<CardResponse> dueCards,
        CancellationToken cancellationToken)
    {
        var dueCardIds = dueCards.Select(c => c.Id).ToHashSet();
        var studyCards = new List<StudyCard>();

        foreach (var card in dueCards)
        {
            var suggestedMode = GetSuggestedMode(card);
            var options = suggestedMode == "mcq"
                ? await BuildMcqOptionsAsync(userId, card.DeckId, card, dueCardIds, cancellationToken)
                : null;

            if (suggestedMode == "mcq" && (options?.Count ?? 0) < 4)
            {
                suggestedMode = "flashcard";
                options = null;
            }

            studyCards.Add(new StudyCard
            {
                CardId = card.Id,
                DeckId = card.DeckId,
                DeckName = card.DeckName,
                Term = card.Term,
                Translation = card.Translation,
                Pronunciation = card.Pronunciation,
                ImageUrl = card.ImageUrl,
                SrsState = card.SrsState,
                SrsRepetitions = card.SrsRepetitions,
                SrsIntervalDays = card.SrsIntervalDays,
                IsFirstReview = card.SrsRepetitions <= 0,
                SuggestedMode = suggestedMode,
                Options = options
            });
        }

        return studyCards;
    }

    private static string GetSuggestedMode(CardResponse card) => "mcq";

    private async Task<List<StudyOption>> BuildMcqOptionsAsync(
        string userId,
        string deckId,
        CardResponse correctCard,
        HashSet<string> excludeIds,
        CancellationToken cancellationToken)
    {
        const int needed = 3;

        var realDistractors = await _firestoreService.GetDistractorCardsAsync(
            userId, deckId, excludeIds, needed, cancellationToken);

        var usedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            correctCard.Term
        };

        var options = realDistractors
            .Where(d => usedTerms.Add(d.Term))
            .Select(d => new StudyOption { Id = d.Id, Term = d.Term, IsCorrect = false })
            .Take(needed)
            .ToList();

        if (options.Count < needed)
        {
            var aiTerms = await GetOrGenerateAiDistractorsAsync(
                userId, deckId, correctCard, needed - options.Count, cancellationToken);

            options.AddRange(aiTerms
                .Where(term => usedTerms.Add(term))
                .Select((term, index) => new StudyOption
                {
                    Id = $"ai_{index}",
                    Term = term,
                    IsCorrect = false
                }));
        }

        options.Add(new StudyOption
        {
            Id = correctCard.Id,
            Term = correctCard.Term,
            IsCorrect = true
        });

        return options.OrderBy(_ => Guid.NewGuid()).ToList();
    }

    private async Task<List<string>> GetOrGenerateAiDistractorsAsync(
        string userId,
        string deckId,
        CardResponse card,
        int countNeeded,
        CancellationToken cancellationToken)
    {
        if (card.SrsDistractors.Count >= countNeeded)
        {
            return card.SrsDistractors.Take(countNeeded).ToList();
        }

        var targetLanguage = card.TargetLangCode switch
        {
            "vi" => "Vietnamese",
            "en" => "English",
            "ja" => "Japanese",
            "ko" => "Korean",
            "zh" => "Chinese",
            "fr" => "French",
            "de" => "German",
            "es" => "Spanish",
            _ => card.TargetLangCode
        };

        List<string> distractors;
        try
        {
            var openAiService = _serviceProvider.GetRequiredService<IOpenAiService>();
            distractors = await openAiService.GenerateDistractorsAsync(
                card.Term, card.Translation, targetLanguage, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "AI distractor generation is unavailable for card {CardId}. Falling back to flashcard mode if needed.",
                card.Id);
            return new List<string>();
        }

        if (distractors.Count > 0)
        {
            try
            {
                await _firestoreService.CacheDistractorsAsync(
                    userId, deckId, card.Id, distractors, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache distractors for card {CardId}.", card.Id);
            }
        }

        return distractors.Take(countNeeded).ToList();
    }

    private static ApiResponse<object> Err(string message) =>
        new() { Status = "error", Message = message };
}
