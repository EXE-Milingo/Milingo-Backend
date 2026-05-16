using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Extensions;
using Milingo.Backend.Models;
using Milingo.Backend.Models.Yolo;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

[ApiController]
[Route("api/v1/snap")]
[Authorize]
public class SnapController : ControllerBase
{
    private readonly IGeminiService _geminiService;
    private readonly IYoloService _yoloService;
    private readonly IFirestoreService _firestoreService;
    private readonly ILogger<SnapController> _logger;

    public SnapController(
        IGeminiService geminiService,
        IYoloService yoloService,
        IFirestoreService firestoreService,
        ILogger<SnapController> logger)
    {
        _geminiService = geminiService;
        _yoloService = yoloService;
        _firestoreService = firestoreService;
        _logger = logger;
    }

    /// <summary>
    /// Accepts an image upload, detects main objects via YOLO, sends each
    /// cropped object to Gemini AI for vocabulary analysis, saves results
    /// to Firestore, and awards coins.
    ///
    /// If YOLO fails or finds no objects, falls back to sending the full
    /// image to Gemini (original behaviour).
    ///
    /// Idempotency: the Idempotency-Key is checked BEFORE any AI calls.
    /// Duplicate requests short-circuit and return the cached result.
    /// </summary>
    [HttpPost("analyze")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB max upload size
    public async Task<IActionResult> AnalyzeSnap(
        IFormFile image,
        CancellationToken cancellationToken)
    {
        try
        {
            // --- 1. SECURITY: Extract UID from verified Firebase JWT ---
            var userId = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Invalid token: User identifier not found in claims."
                });
            }

            // --- 2. IDEMPOTENCY KEY: Extract from request header ---
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

            // --- 3. IDEMPOTENCY CHECK: Return cached result if duplicate ---
            // This runs BEFORE any expensive YOLO / Gemini calls
            var cachedResult = await _firestoreService.GetCachedSnapResultAsync(
                userId, idempotencyKey, cancellationToken);

            if (cachedResult is not null)
            {
                _logger.LogInformation(
                    "Returning cached idempotent response for user '{UserId}', key '{Key}'.",
                    userId, idempotencyKey);

                return Ok(new ApiResponse<SnapAnalysisResponse>
                {
                    Status = "success",
                    Message = "This request was already processed. No duplicate coins awarded.",
                    Data = cachedResult
                });
            }

            // --- 4. VALIDATION: Check file presence ---
            if (image is null || image.Length == 0)
            {
                return BadRequest(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "An image file is required."
                });
            }

            // --- 5. VALIDATION: MIME type allowlist (first pass) ---
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

            // --- 6. SECURITY: Magic number validation (second pass) ---
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

            // --- 7. BUFFER IMAGE: Read once for YOLO + Gemini fallback ---
            byte[] imageBytes;
            using (var ms = new MemoryStream())
            {
                await image.OpenReadStream().CopyToAsync(ms, cancellationToken);
                imageBytes = ms.ToArray();
            }

            _logger.LogInformation("User '{UserId}' is analyzing an image ({Size} bytes).",
                userId, imageBytes.Length);

            // --- 8. YOLO DETECTION: Try to detect main objects ---
            YoloDetectionResponse? yoloResult = null;
            using (var yoloStream = new MemoryStream(imageBytes))
            {
                yoloResult = await _yoloService.DetectObjectsAsync(
                    yoloStream, image.FileName ?? "image.jpg", image.ContentType, cancellationToken);
            }

            var hasDetections = yoloResult is not null
                && yoloResult.Success
                && yoloResult.Objects.Count > 0;

            // --- 9. AI ANALYSIS: Per-object or full-image fallback ---
            List<SnapVocabItem> vocabItems;
            bool usedFallback;
            var detectionDetails = new List<SnapDetectionDetail>();

            if (hasDetections)
            {
                // -- MULTI-OBJECT PATH: Analyse each cropped object --
                usedFallback = false;
                vocabItems = await AnalyzeCroppedObjectsAsync(yoloResult!.Objects, cancellationToken);

                // Capture detection details for Firestore
                detectionDetails = yoloResult.Objects.Select(o => new SnapDetectionDetail
                {
                    Label = o.Label,
                    Confidence = o.Confidence,
                    X = o.BoundingBox.X,
                    Y = o.BoundingBox.Y,
                    Width = o.BoundingBox.Width,
                    Height = o.BoundingBox.Height,
                    Segmentation = ToSnapSegmentation(o.Segmentation)
                }).ToList();

                // If Gemini failed for ALL objects, fall back to full image
                if (vocabItems.Count == 0)
                {
                    _logger.LogWarning(
                        "Gemini failed for all {Count} YOLO objects, falling back to full image.",
                        yoloResult.Objects.Count);

                    usedFallback = true;
                    detectionDetails.Clear();
                    vocabItems = await AnalyzeFullImageFallbackAsync(
                        imageBytes, image.ContentType, cancellationToken);
                }
            }
            else
            {
                // -- FALLBACK PATH: Send full image to Gemini --
                _logger.LogInformation("YOLO returned no valid objects, using full-image fallback.");
                usedFallback = true;
                vocabItems = await AnalyzeFullImageFallbackAsync(
                    imageBytes, image.ContentType, cancellationToken);
            }

            // --- 10. PERSISTENCE: Save vocabs, coins, cache response (with idempotency) ---
            var isNew = await _firestoreService.SaveMultiVocabAndAddCoinsAsync(
                userId, vocabItems, idempotencyKey, usedFallback,
                detectionDetails, cancellationToken);

            var coinsAwarded = isNew ? vocabItems.Count * 10 : 0;

            // --- 11. RESPONSE (backward compatible) ---
            var responseData = BuildResponse(idempotencyKey, vocabItems, usedFallback, coinsAwarded);

            var message = isNew
                ? $"Analyzed {vocabItems.Count} object(s). +{coinsAwarded} coins!"
                : "This request was already processed. No duplicate coins awarded.";

            _logger.LogInformation(
                "User '{UserId}' snap complete: {Count} object(s), fallback={Fallback}, new={IsNew}.",
                userId, vocabItems.Count, usedFallback, isNew);

            return Ok(new ApiResponse<SnapAnalysisResponse>
            {
                Status = "success",
                Message = message,
                Data = responseData
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Request cancelled: client disconnected.");
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

    // =================================================================
    //  PRIVATE HELPERS
    // =================================================================

    /// <summary>
    /// Builds a <see cref="SnapAnalysisResponse"/> with backward-compatible
    /// legacy fields populated from the first vocab item.
    /// </summary>
    private static SnapAnalysisResponse BuildResponse(
        string snapGroupId,
        List<SnapVocabItem> vocabItems,
        bool usedFallback,
        int coinsAwarded)
    {
        var first = vocabItems.FirstOrDefault();

        return new SnapAnalysisResponse
        {
            // Legacy fields (backward compat with Flutter expecting VocabResponse shape)
            Keyword = first?.Keyword ?? string.Empty,
            Translation = first?.Translation ?? string.Empty,
            Pronunciation = first?.Pronunciation ?? string.Empty,
            ExampleSentence = first?.ExampleSentence ?? string.Empty,

            // New multi-object fields
            SnapGroupId = snapGroupId,
            ObjectCount = vocabItems.Count,
            UsedFallback = usedFallback,
            VocabItems = vocabItems,
            CoinsAwarded = coinsAwarded
        };
    }

    /// <summary>
    /// Sends each YOLO-detected cropped object to Gemini in parallel.
    /// Skips individual failures so partial results are still returned.
    /// </summary>
    private async Task<List<SnapVocabItem>> AnalyzeCroppedObjectsAsync(
        List<DetectedObject> detections,
        CancellationToken cancellationToken)
    {
        var tasks = detections.Select(async det =>
        {
            try
            {
                var vocab = await _geminiService.AnalyzeBase64ImageAsync(
                    det.CroppedImageBase64,
                    "image/jpeg", // Crops are always JPEG from YOLO service
                    det.Label,
                    cancellationToken);

                return new SnapVocabItem
                {
                    Keyword = vocab.Keyword,
                    Translation = vocab.Translation,
                    Pronunciation = vocab.Pronunciation,
                    ExampleSentence = vocab.ExampleSentence,
                    DetectionLabel = det.Label,
                    DetectionConfidence = det.Confidence,
                    BoundingBox = new SnapBoundingBox
                    {
                        X = det.BoundingBox.X,
                        Y = det.BoundingBox.Y,
                        Width = det.BoundingBox.Width,
                        Height = det.BoundingBox.Height
                    },
                    Segmentation = ToSnapSegmentation(det.Segmentation),
                    CroppedImageBase64 = det.CroppedImageBase64
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Propagate client disconnection
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Gemini analysis failed for YOLO object '{Label}' (confidence {Conf}). Skipping.",
                    det.Label, det.Confidence);
                return null; // Skip this object
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).ToList()!;
    }

    private static SnapSegmentation? ToSnapSegmentation(YoloSegmentation? segmentation)
    {
        if (segmentation?.Points is not { Count: >= 3 })
            return null;

        return new SnapSegmentation
        {
            Points = segmentation.Points
                .Select(p => new SnapSegmentationPoint { X = p.X, Y = p.Y })
                .ToList()
        };
    }

    /// <summary>
    /// Fallback: sends the full original image to Gemini (original single-object behaviour).
    /// </summary>
    private async Task<List<SnapVocabItem>> AnalyzeFullImageFallbackAsync(
        byte[] imageBytes,
        string mimeType,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(imageBytes);
        var vocab = await _geminiService.AnalyzeImageAsync(stream, mimeType, cancellationToken);

        return new List<SnapVocabItem>
        {
            new()
            {
                Keyword = vocab.Keyword,
                Translation = vocab.Translation,
                Pronunciation = vocab.Pronunciation,
                ExampleSentence = vocab.ExampleSentence,
                DetectionLabel = null,
                DetectionConfidence = null
            }
        };
    }

    // =================================================================
    //  MAGIC NUMBER VALIDATION
    // =================================================================

    /// <summary>
    /// Reads the first bytes of the uploaded file and compares them against
    /// known file signatures (magic numbers) for JPEG, PNG, and WebP.
    /// </summary>
    private static bool IsValidImageSignature(IFormFile file)
    {
        const int headerSize = 12;
        var header = new byte[headerSize];

        using var stream = file.OpenReadStream();
        var bytesRead = stream.Read(header, 0, headerSize);

        if (bytesRead < 3)
            return false;

        // JPEG: starts with FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true;

        // PNG: starts with 89 50 4E 47 0D 0A 1A 0A (8 bytes)
        if (bytesRead >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 &&
            header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A &&
            header[6] == 0x1A && header[7] == 0x0A)
            return true;

        // WebP: bytes 0-3 = "RIFF", bytes 8-11 = "WEBP"
        if (bytesRead >= 12 &&
            header[0] == 0x52 && header[1] == 0x49 &&   // "RI"
            header[2] == 0x46 && header[3] == 0x46 &&   // "FF"
            header[8] == 0x57 && header[9] == 0x45 &&   // "WE"
            header[10] == 0x42 && header[11] == 0x50)   // "BP"
            return true;

        return false;
    }
}
