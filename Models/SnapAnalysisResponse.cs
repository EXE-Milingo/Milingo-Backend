using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

/// <summary>
/// Represents a single vocabulary item enriched with detection metadata.
/// Used inside <see cref="SnapAnalysisResponse"/>.
/// </summary>
public class SnapVocabItem
{
    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [JsonPropertyName("example_sentence")]
    public string ExampleSentence { get; set; } = string.Empty;

    /// <summary>
    /// The YOLO detection label (e.g. "laptop"). Null when fallback was used.
    /// </summary>
    [JsonPropertyName("detection_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DetectionLabel { get; set; }

    /// <summary>
    /// YOLO confidence score. Null when fallback was used.
    /// </summary>
    [JsonPropertyName("detection_confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DetectionConfidence { get; set; }

    /// <summary>
    /// YOLO bounding box in original image pixels. Null when fallback was used.
    /// </summary>
    [JsonPropertyName("bounding_box")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnapBoundingBox? BoundingBox { get; set; }

    /// <summary>
    /// YOLO segmentation contour in original image pixels. Null when the
    /// segmenter did not return a usable mask.
    /// </summary>
    [JsonPropertyName("segmentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnapSegmentation? Segmentation { get; set; }

    /// <summary>
    /// Base64-encoded JPEG of the cropped object detected by YOLO.
    /// Null when fallback (full-image) was used.
    /// Flutter uses this to display the object with a decorative border
    /// and upload it to Firebase Cloud Storage.
    /// </summary>
    [JsonPropertyName("cropped_image_base64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CroppedImageBase64 { get; set; }
}

public class SnapBoundingBox
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public class SnapSegmentation
{
    [JsonPropertyName("points")]
    public List<SnapSegmentationPoint> Points { get; set; } = new();
}

public class SnapSegmentationPoint
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }
}

/// <summary>
/// Per-object YOLO detection detail stored in Firestore snap_events.
/// Captures the raw bounding box and confidence for audit / debugging.
/// </summary>
public class SnapDetectionDetail
{
    public string Label { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public SnapSegmentation? Segmentation { get; set; }
}

/// <summary>
/// Object returned by the detect-only snap endpoint before OpenAI analysis.
/// </summary>
public class SnapDetectedObject
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("bounding_box")]
    public SnapBoundingBox BoundingBox { get; set; } = new();

    [JsonPropertyName("segmentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnapSegmentation? Segmentation { get; set; }

    [JsonPropertyName("cropped_image_base64")]
    public string CroppedImageBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Response for /api/v1/snap/detect. Contains YOLO data only.
/// </summary>
public class SnapDetectionResponse
{
    [JsonPropertyName("objects")]
    public List<SnapDetectedObject> Objects { get; set; } = new();

    [JsonPropertyName("total_detected")]
    public int TotalDetected { get; set; }

    [JsonPropertyName("returned_count")]
    public int ReturnedCount { get; set; }

    [JsonPropertyName("processing_time_ms")]
    public double ProcessingTimeMs { get; set; }
}

/// <summary>
/// Request for /api/v1/snap/analyze-detected. OpenAI receives only approved
/// YOLO crops, so analysis does not re-run detection.
/// </summary>
public class AnalyzeDetectedSnapRequest
{
    [JsonPropertyName("objects")]
    public List<SnapDetectedObject> Objects { get; set; } = new();
}

/// <summary>
/// Top-level response for the /api/v1/snap/analyze endpoint.
/// <para>
/// <strong>Backward compatibility:</strong> When exactly one object is detected
/// (including fallback), the legacy fields (<c>keyword</c>, <c>translation</c>,
/// <c>pronunciation</c>, <c>example_sentence</c>) are populated at the top level
/// so existing Flutter clients that expect a single <c>VocabResponse</c> shape
/// continue to work without changes.
/// </para>
/// </summary>
public class SnapAnalysisResponse
{
    // ── Legacy single-object fields (backward compatible) ──────────

    /// <summary>
    /// Primary keyword. Always populated (first vocab item).
    /// Matches the original VocabResponse.keyword field.
    /// </summary>
    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [JsonPropertyName("example_sentence")]
    public string ExampleSentence { get; set; } = string.Empty;

    // ── New multi-object fields ───────────────────────────────────

    /// <summary>
    /// Groups all vocabulary items from this snap together in Firestore.
    /// Equals the Idempotency-Key sent by the client.
    /// </summary>
    [JsonPropertyName("snap_group_id")]
    public string SnapGroupId { get; set; } = string.Empty;

    /// <summary>
    /// Number of objects analysed.
    /// </summary>
    [JsonPropertyName("object_count")]
    public int ObjectCount { get; set; }

    /// <summary>
    /// Whether the YOLO service was bypassed (no detections / service down).
    /// </summary>
    [JsonPropertyName("used_fallback")]
    public bool UsedFallback { get; set; }

    /// <summary>
    /// The vocabulary items extracted from the photo.
    /// </summary>
    [JsonPropertyName("vocab_items")]
    public List<SnapVocabItem> VocabItems { get; set; } = new();

    /// <summary>
    /// Total coins awarded for this snap (10 per vocab item).
    /// </summary>
    [JsonPropertyName("coins_awarded")]
    public int CoinsAwarded { get; set; }
}
