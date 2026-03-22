using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

[ApiController]
[Route("api/v1/snap")]
[Authorize] // All endpoints require a valid Firebase JWT
public class SnapController : ControllerBase
{
    private readonly IGeminiService _geminiService;
    private readonly IFirestoreService _firestoreService;
    private readonly ILogger<SnapController> _logger;

    // Services are injected via DI — no more "new GeminiService()"
    public SnapController(
        IGeminiService geminiService,
        IFirestoreService firestoreService,
        ILogger<SnapController> logger)
    {
        _geminiService = geminiService;
        _firestoreService = firestoreService;
        _logger = logger;
    }

    /// <summary>
    /// Accepts an image upload, sends it to Gemini AI for vocabulary analysis,
    /// saves the result to Firestore, and awards coins to the user.
    /// </summary>
    /// <param name="image">The image file uploaded as multipart/form-data.</param>
    /// <param name="cancellationToken">
    /// Automatically bound by ASP.NET Core from <c>HttpContext.RequestAborted</c>.
    /// Fires when the Flutter client disconnects.
    /// </param>
    [HttpPost("analyze")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB max upload size
    public async Task<IActionResult> AnalyzeSnap(
        IFormFile image,
        CancellationToken cancellationToken)
    {
        try
        {
            // ─── 1. SECURITY: Extract UID from verified Firebase JWT ───
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid token: User identifier not found in claims."
                });
            }

            // ─── 2. IDEMPOTENCY KEY: Extract from request header ───
            if (!Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKeyValues)
                || string.IsNullOrWhiteSpace(idempotencyKeyValues.FirstOrDefault()))
            {
                return BadRequest(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "The 'Idempotency-Key' header is required. Send a unique UUID per snap request."
                });
            }
            var idempotencyKey = idempotencyKeyValues.First()!.Trim();

            // ─── 3. VALIDATION: Check file presence ───
            if (image is null || image.Length == 0)
            {
                return BadRequest(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "An image file is required."
                });
            }

            // ─── 4. VALIDATION: MIME type allowlist (first pass) ───
            var allowedMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpeg", "image/png", "image/webp"
            };

            if (!allowedMimeTypes.Contains(image.ContentType))
            {
                return BadRequest(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid file type. Allowed types: JPEG, PNG, WebP."
                });
            }

            // ─── 5. SECURITY: Magic number validation (second pass) ───
            // The MIME type from the client can be spoofed.
            // Read the first bytes of the actual file to verify its true format.
            if (!IsValidImageSignature(image))
            {
                _logger.LogWarning(
                    "User '{UserId}' uploaded a file with spoofed Content-Type '{ContentType}'.",
                    userId, image.ContentType);

                return BadRequest(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "The uploaded file is not a valid image. File signature mismatch."
                });
            }

            // ─── 6. AI ANALYSIS: Send image to Gemini ───
            _logger.LogInformation("User '{UserId}' is analyzing an image ({Size} bytes).",
                userId, image.Length);

            using var stream = image.OpenReadStream();
            var aiResult = await _geminiService.AnalyzeImageAsync(
                stream, image.ContentType, cancellationToken);

            // ─── 7. PERSISTENCE: Save vocab & award coins (with idempotency) ───
            var isNew = await _firestoreService.SaveVocabAndAddCoinsAsync(
                userId, aiResult, idempotencyKey, cancellationToken);

            // ─── 8. RESPONSE ───
            if (!isNew)
            {
                // Idempotent: this key was already processed — return success
                // without double-awarding coins. This is standard idempotent behavior.
                _logger.LogInformation(
                    "Returning idempotent response for user '{UserId}', key '{Key}'.",
                    userId, idempotencyKey);

                return Ok(new ApiResponse<VocabResponse>
                {
                    Status = "success",
                    Message = "This request was already processed. No duplicate coins awarded.",
                    Data = aiResult
                });
            }

            _logger.LogInformation("User '{UserId}' successfully analyzed: '{Keyword}'.",
                userId, aiResult.Keyword);

            return Ok(new ApiResponse<VocabResponse>
            {
                Status = "success",
                Message = "Image analyzed and saved successfully. +10 coins!",
                Data = aiResult
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The Flutter client disconnected — no point returning a response.
            // Log it and let ASP.NET Core handle the connection closure.
            _logger.LogInformation("Request cancelled — client disconnected.");
            return StatusCode(499); // 499 Client Closed Request (nginx convention)
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Gemini API call failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new ApiResponse<object>
            {
                Status = "error",
                Message = "The AI service is currently unavailable. Please try again later."
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AI returned an invalid or incomplete response.");
            return UnprocessableEntity(new ApiResponse<object>
            {
                Status = "error",
                Message = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during snap analysis.");
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred. Please try again."
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  MAGIC NUMBER VALIDATION — Verify actual file bytes, not
    //  the client-provided Content-Type which can be spoofed.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the first bytes of the uploaded file and compares them against
    /// known file signatures (magic numbers) for JPEG, PNG, and WebP.
    /// </summary>
    private static bool IsValidImageSignature(IFormFile file)
    {
        // We need at most 12 bytes to identify all three formats
        const int headerSize = 12;
        var header = new byte[headerSize];

        using var stream = file.OpenReadStream();
        var bytesRead = stream.Read(header, 0, headerSize);

        if (bytesRead < 3)
            return false;

        // ── JPEG: starts with FF D8 FF ──
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true;

        // ── PNG: starts with 89 50 4E 47 0D 0A 1A 0A (8 bytes) ──
        if (bytesRead >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 &&
            header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A &&
            header[6] == 0x1A && header[7] == 0x0A)
            return true;

        // ── WebP: bytes 0-3 = "RIFF", bytes 8-11 = "WEBP" ──
        if (bytesRead >= 12 &&
            header[0] == 0x52 && header[1] == 0x49 &&   // "RI"
            header[2] == 0x46 && header[3] == 0x46 &&   // "FF"
            header[8] == 0x57 && header[9] == 0x45 &&   // "WE"
            header[10] == 0x42 && header[11] == 0x50)   // "BP"
            return true;

        return false;
    }
}