using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Extensions;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controller;

/// <summary>
/// AI Tutor conversational chat endpoint.
/// All routes require a valid Firebase JWT.
///
/// Access rules:
///   - Premium users: unlimited daily messages.
///   - Free users: up to <see cref="_freeDailyLimit"/> messages per day (Vietnam local calendar).
/// </summary>
[ApiController]
[Route("api/v1/ai-tutor")]
[Authorize]
public sealed class AiTutorController : ControllerBase
{
    private const int MaxMessageLength = 500;
    private const int MaxHistoryItems = 40; // 20 turns × 2 messages each

    private readonly IChatService _chatService;
    private readonly IFirestoreService _firestoreService;
    private readonly ILogger<AiTutorController> _logger;
    private readonly int _freeDailyLimit;

    public AiTutorController(
        IChatService chatService,
        IFirestoreService firestoreService,
        IConfiguration configuration,
        ILogger<AiTutorController> logger)
    {
        _chatService = chatService;
        _firestoreService = firestoreService;
        _logger = logger;
        _freeDailyLimit = configuration.GetValue<int>("AiTutor:FreeDailyLimit", 20);
    }

    // ──────────────────────────────────────────────────────────────
    //  GET /api/v1/ai-tutor/quota
    // ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Returns the current daily AI Tutor quota for the authenticated user.
    /// Premium users have unlimited access; free users see remaining message count.
    /// </summary>
    [HttpGet("quota")]
    [ProducesResponseType(typeof(ApiResponse<ChatQuotaInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetQuota(CancellationToken ct)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new ApiResponse<object> { Status = "error", Message = "Invalid token." });

        try
        {
            var quota = await _firestoreService.GetChatQuotaStatusAsync(userId, _freeDailyLimit, ct);
            return Ok(new ApiResponse<ChatQuotaInfo>
            {
                Status = "success",
                Message = "OK",
                Data = quota
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Tutor: Error fetching quota for user {UserId}.", userId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ApiResponse<object> { Status = "error", Message = "Error fetching quota." });
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  POST /api/v1/ai-tutor/chat
    // ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Send a user message and receive the AI Tutor's reply.
    /// Premium users bypass the daily limit. Free users are limited to
    /// <see cref="_freeDailyLimit"/> messages per day (Vietnam time).
    /// </summary>
    [HttpPost("chat")]
    [ProducesResponseType(typeof(ApiResponse<ChatResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<ChatQuotaInfo>), StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Chat(
        [FromBody] ChatRequest request,
        CancellationToken ct)
    {
        // ── 1. Resolve authenticated user ──────────────────────────
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new ApiResponse<object> { Status = "error", Message = "Invalid token." });

        // ── 2. Input validation ────────────────────────────────────
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new ApiResponse<object>
            {
                Status = "error",
                Message = "Message cannot be empty."
            });
        }

        if (request.Message.Length > MaxMessageLength)
        {
            return BadRequest(new ApiResponse<object>
            {
                Status = "error",
                Message = $"Message exceeds the maximum allowed length of {MaxMessageLength} characters."
            });
        }

        // ── 3. Quota check (premium bypass / free limit) ───────────
        ChatQuotaInfo quota;
        try
        {
            quota = await _firestoreService.GetChatQuotaStatusAsync(userId, _freeDailyLimit, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Tutor: Quota check failed for user {UserId}.", userId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ApiResponse<object> { Status = "error", Message = "Could not verify quota. Please try again." });
        }

        if (quota.IsLimitReached)
        {
            _logger.LogInformation(
                "AI Tutor: Free user {UserId} hit daily limit ({Limit}).", userId, _freeDailyLimit);
            return StatusCode(StatusCodes.Status402PaymentRequired, new ApiResponse<ChatQuotaInfo>
            {
                Status = "error",
                Message = $"Bạn đã dùng hết {_freeDailyLimit} tin nhắn miễn phí hôm nay. " +
                          "Nâng cấp lên Premium để nhắn không giới hạn!",
                Data = quota
            });
        }

        // ── 4. Trim history ────────────────────────────────────────
        var history = (request.History ?? [])
            .TakeLast(MaxHistoryItems)
            .ToList();

        var targetLanguage = string.IsNullOrWhiteSpace(request.TargetLanguage)
            ? "en"
            : request.TargetLanguage.Trim();

        var cefrLevel = string.IsNullOrWhiteSpace(request.CefrLevel)
            ? "A1"
            : request.CefrLevel.Trim().ToUpper();

        // ── 5. Call the AI ─────────────────────────────────────────
        string reply;
        try
        {
            reply = await _chatService.ChatAsync(
                userId,
                request.Message.Trim(),
                history,
                targetLanguage,
                cefrLevel,
                ct);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("AI Tutor chat cancelled for user {UserId}.", userId);
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AI Tutor: Upstream service error for user {UserId}.", userId);
            return StatusCode(StatusCodes.Status502BadGateway, new ApiResponse<object>
            {
                Status = "error",
                Message = "AI service is temporarily unavailable. Please try again."
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "AI Tutor: Response parsing error for user {UserId}.", userId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiResponse<object>
            {
                Status = "error",
                Message = "AI returned an unexpected response. Please try again."
            });
        }

        // ── 6. Increment usage counter (fire-and-forget for premium, counted for free) ──
        ChatQuotaInfo updatedQuota;
        try
        {
            updatedQuota = await _firestoreService.IncrementChatUsageAsync(userId, _freeDailyLimit, ct);
        }
        catch (Exception ex)
        {
            // Non-critical — log and continue; the reply is already generated
            _logger.LogWarning(ex, "AI Tutor: Failed to increment usage counter for user {UserId}.", userId);
            updatedQuota = quota; // return pre-call quota as fallback
        }

        // ── 7. Return response ─────────────────────────────────────
        return Ok(new ApiResponse<ChatResponse>
        {
            Status = "success",
            Message = "OK",
            Data = new ChatResponse
            {
                Reply = reply,
                Role = "assistant",
                Quota = updatedQuota
            }
        });
    }
}
