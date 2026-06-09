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
    private readonly IOpenAiService _openAiService;
    private readonly IYoloService _yoloService;
    private readonly IFirestoreService _firestoreService;
    private readonly ILogger<SnapController> _logger;

    public SnapController(
        IOpenAiService openAiService,
        IYoloService yoloService,
        IFirestoreService firestoreService,
        ILogger<SnapController> logger)
    {
        _openAiService = openAiService;
        _yoloService = yoloService;
        _firestoreService = firestoreService;
        _logger = logger;
    }

    /// <summary>
    /// Accepts an image upload and returns YOLO segmentation/crop data only.
    /// OpenAI analysis is intentionally not called here; the client confirms
    /// the detected object first, then calls /analyze-detected.
    /// </summary>
    [HttpPost("detect")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> DetectSnap(
        IFormFile image,
        CancellationToken cancellationToken)
    {
        try
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

            if (image is null || image.Length == 0)
            {
                return BadRequest(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "An image file is required."
                });
            }

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

            byte[] imageBytes;
            using (var ms = new MemoryStream())
            {
                await image.OpenReadStream().CopyToAsync(ms, cancellationToken);
                imageBytes = ms.ToArray();
            }

            YoloDetectionResponse? yoloResult;
            using (var yoloStream = new MemoryStream(imageBytes))
            {
                yoloResult = await _yoloService.DetectObjectsAsync(
                    yoloStream, image.FileName ?? "image.jpg", image.ContentType, cancellationToken);
            }

            if (yoloResult is null || !yoloResult.Success || yoloResult.Objects.Count == 0)
            {
                return UnprocessableEntity(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "Could not segment a clear object. Please try another photo."
                });
            }

            var responseData = new SnapDetectionResponse
            {
                Objects = yoloResult.Objects.Select(ToSnapDetectedObject).ToList(),
                TotalDetected = yoloResult.TotalDetected,
                ReturnedCount = yoloResult.ReturnedCount,
                ProcessingTimeMs = yoloResult.ProcessingTimeMs
            };

            _logger.LogInformation(
                "User '{UserId}' detected {Count} object(s); waiting for client confirmation.",
                userId, responseData.Objects.Count);

            return Ok(new ApiResponse<SnapDetectionResponse>
            {
                Status = "success",
                Message = $"Detected {responseData.Objects.Count} object(s).",
                Data = responseData
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Detect request cancelled: client disconnected.");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during snap detection.");
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiResponse<object>
            {
                Status = "error",
                Message = "An unexpected error occurred while detecting the object."
            });
        }
    }

    /// <summary>
    /// Accepts an image upload, detects main objects via YOLO, sends each
    /// cropped object to OpenAI for vocabulary analysis, saves results
    /// to Firestore, and awards coins.
    ///
    /// If YOLO fails or finds no objects, falls back to sending the full
    /// image to OpenAI.
    ///
    /// Idempotency: the Idempotency-Key is checked BEFORE any AI calls.
    /// Duplicate requests short-circuit and return the cached result.
    /// </summary>
    [HttpPost("analyze")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB max upload size
    public async Task<IActionResult> AnalyzeSnap(
        IFormFile image,
        [FromForm] string? targetLanguage,
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
            var resolvedTargetLanguage = await ResolveTargetLanguageAsync(
                targetLanguage, userId, cancellationToken);

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
            // This runs BEFORE any expensive YOLO / OpenAI calls
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

            // --- 7. BUFFER IMAGE: Read once for YOLO + OpenAI fallback ---
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
                vocabItems = await AnalyzeCroppedObjectsAsync(
                    yoloResult!.Objects, resolvedTargetLanguage, cancellationToken);

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

                // If OpenAI failed for ALL objects, fall back to full image
                if (vocabItems.Count == 0)
                {
                    _logger.LogWarning(
                        "OpenAI failed for all {Count} YOLO objects, falling back to full image.",
                        yoloResult.Objects.Count);

                    usedFallback = true;
                    detectionDetails.Clear();
                    vocabItems = await AnalyzeFullImageFallbackAsync(
                        imageBytes, image.ContentType, resolvedTargetLanguage, cancellationToken);
                }
            }
            else
            {
                // -- FALLBACK PATH: Send full image to OpenAI --
                _logger.LogInformation("YOLO returned no valid objects, using full-image fallback.");
                usedFallback = true;
                vocabItems = await AnalyzeFullImageFallbackAsync(
                    imageBytes, image.ContentType, resolvedTargetLanguage, cancellationToken);
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
            _logger.LogError(ex, "OpenAI API call failed.");
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

    /// <summary>
    /// Analyzes client-approved YOLO crops without running detection again.
    /// </summary>
    [HttpPost("analyze-detected")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> AnalyzeDetectedSnap(
        [FromBody] AnalyzeDetectedSnapRequest request,
        CancellationToken cancellationToken)
    {
        try
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

            var cachedResult = await _firestoreService.GetCachedSnapResultAsync(
                userId, idempotencyKey, cancellationToken);

            if (cachedResult is not null)
            {
                return Ok(new ApiResponse<SnapAnalysisResponse>
                {
                    Status = "success",
                    Message = "This request was already processed. No duplicate coins awarded.",
                    Data = cachedResult
                });
            }

            var detections = request?.Objects
                .Where(o => !string.IsNullOrWhiteSpace(o.CroppedImageBase64))
                .Take(3)
                .ToList() ?? new List<SnapDetectedObject>();

            if (detections.Count == 0)
            {
                return BadRequest(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "At least one detected object crop is required."
                });
            }

            var resolvedTargetLanguage = await ResolveTargetLanguageAsync(
                request?.TargetLanguage, userId, cancellationToken);
            var vocabItems = await AnalyzeDetectedObjectsAsync(
                detections, resolvedTargetLanguage, cancellationToken);
            if (vocabItems.Count == 0)
            {
                return UnprocessableEntity(new ApiResponse<object>
                {
                    Status = "error",
                    Message = "The AI could not identify the confirmed object. Please try another photo."
                });
            }

            var detectionDetails = detections.Select(ToSnapDetectionDetail).ToList();
            var isNew = await _firestoreService.SaveMultiVocabAndAddCoinsAsync(
                userId, vocabItems, idempotencyKey, usedFallback: false,
                detectionDetails, cancellationToken);

            var coinsAwarded = isNew ? vocabItems.Count * 10 : 0;
            var responseData = BuildResponse(idempotencyKey, vocabItems, usedFallback: false, coinsAwarded);

            _logger.LogInformation(
                "User '{UserId}' confirmed snap complete: {Count} object(s), new={IsNew}.",
                userId, vocabItems.Count, isNew);

            return Ok(new ApiResponse<SnapAnalysisResponse>
            {
                Status = "success",
                Message = isNew
                    ? $"Analyzed {vocabItems.Count} object(s). +{coinsAwarded} coins!"
                    : "This request was already processed. No duplicate coins awarded.",
                Data = responseData
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Analyze-detected request cancelled: client disconnected.");
            return StatusCode(499);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "OpenAI API call failed.");
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
            _logger.LogError(ex, "Unexpected error during confirmed snap analysis.");
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
    /// Sends each YOLO-detected cropped object to OpenAI in parallel.
    /// Skips individual failures so partial results are still returned.
    /// </summary>
    private async Task<List<SnapVocabItem>> AnalyzeCroppedObjectsAsync(
        List<DetectedObject> detections,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var tasks = detections.Select(async det =>
        {
            try
            {
                var vocab = await _openAiService.AnalyzeBase64ImageAsync(
                    det.CroppedImageBase64,
                    "image/jpeg", // Crops are always JPEG from YOLO service
                    det.Label,
                    targetLanguage,
                    cancellationToken);

                return new SnapVocabItem
                {
                    Keyword = vocab.Keyword,
                    Translation = vocab.Translation,
                    Pronunciation = vocab.Pronunciation,
                    ExampleSentence = vocab.ExampleSentence,
                    RelatedWords = vocab.RelatedWords,
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
                    "OpenAI analysis failed for YOLO object '{Label}' (confidence {Conf}). Skipping.",
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

    private static SnapDetectedObject ToSnapDetectedObject(DetectedObject detection)
    {
        return new SnapDetectedObject
        {
            Label = detection.Label,
            Confidence = detection.Confidence,
            BoundingBox = new SnapBoundingBox
            {
                X = detection.BoundingBox.X,
                Y = detection.BoundingBox.Y,
                Width = detection.BoundingBox.Width,
                Height = detection.BoundingBox.Height
            },
            Segmentation = ToSnapSegmentation(detection.Segmentation),
            CroppedImageBase64 = detection.CroppedImageBase64
        };
    }

    private static SnapDetectionDetail ToSnapDetectionDetail(SnapDetectedObject detection)
    {
        return new SnapDetectionDetail
        {
            Label = detection.Label,
            Confidence = detection.Confidence,
            X = detection.BoundingBox.X,
            Y = detection.BoundingBox.Y,
            Width = detection.BoundingBox.Width,
            Height = detection.BoundingBox.Height,
            Segmentation = detection.Segmentation
        };
    }

    /// <summary>
    /// Sends client-approved cropped objects to OpenAI in parallel.
    /// </summary>
    private async Task<List<SnapVocabItem>> AnalyzeDetectedObjectsAsync(
        List<SnapDetectedObject> detections,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var tasks = detections.Select(async det =>
        {
            try
            {
                var label = string.IsNullOrWhiteSpace(det.Label) ? "object" : det.Label;
                var vocab = await _openAiService.AnalyzeBase64ImageAsync(
                    det.CroppedImageBase64,
                    "image/jpeg",
                    label,
                    targetLanguage,
                    cancellationToken);

                return new SnapVocabItem
                {
                    Keyword = vocab.Keyword,
                    Translation = vocab.Translation,
                    Pronunciation = vocab.Pronunciation,
                    ExampleSentence = vocab.ExampleSentence,
                    RelatedWords = vocab.RelatedWords,
                    DetectionLabel = det.Label,
                    DetectionConfidence = det.Confidence,
                    BoundingBox = det.BoundingBox,
                    Segmentation = det.Segmentation,
                    CroppedImageBase64 = det.CroppedImageBase64
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OpenAI analysis failed for confirmed object '{Label}' (confidence {Conf}). Skipping.",
                    det.Label, det.Confidence);
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).ToList()!;
    }

    /// <summary>
    /// Fallback: sends the full original image to OpenAI.
    /// </summary>
    private async Task<List<SnapVocabItem>> AnalyzeFullImageFallbackAsync(
        byte[] imageBytes,
        string mimeType,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(imageBytes);
        var vocab = await _openAiService.AnalyzeImageAsync(
            stream, mimeType, targetLanguage, cancellationToken);

        return new List<SnapVocabItem>
        {
            new()
            {
                Keyword = vocab.Keyword,
                Translation = vocab.Translation,
                Pronunciation = vocab.Pronunciation,
                ExampleSentence = vocab.ExampleSentence,
                RelatedWords = vocab.RelatedWords,
                DetectionLabel = null,
                DetectionConfidence = null
            }
        };
    }

    private static string ResolveTargetLanguage(string? targetLanguage)
    {
        return string.IsNullOrWhiteSpace(targetLanguage)
            ? "en"
            : targetLanguage.Trim();
    }

    private async Task<string> ResolveTargetLanguageAsync(
        string? targetLanguage,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(targetLanguage))
            return targetLanguage.Trim();

        var profile = await _firestoreService.GetUserProfileAsync(userId, cancellationToken);
        return ResolveTargetLanguage(profile?.TargetLanguage);
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
