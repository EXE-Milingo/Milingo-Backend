using System.Text.Json.Serialization;

namespace Milingo.Backend.Models.Yolo;

/// <summary>
/// Represents a single object detected by the YOLO microservice,
/// including its bounding box and a base64-encoded cropped image.
/// Property names use camelCase to match the YOLO API contract.
/// </summary>
public class DetectedObject
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("boundingBox")]
    public BoundingBox BoundingBox { get; set; } = new();

    [JsonPropertyName("segmentation")]
    public YoloSegmentation? Segmentation { get; set; }

    /// <summary>
    /// JPEG image of the cropped region, encoded as base64.
    /// </summary>
    [JsonPropertyName("croppedImageBase64")]
    public string CroppedImageBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Bounding box coordinates and dimensions (pixels).
/// </summary>
public class BoundingBox
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

/// <summary>
/// Segmentation contour returned by YOLOv8-seg in original-image pixels.
/// </summary>
public class YoloSegmentation
{
    [JsonPropertyName("points")]
    public List<YoloSegmentationPoint> Points { get; set; } = new();
}

public class YoloSegmentationPoint
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }
}
