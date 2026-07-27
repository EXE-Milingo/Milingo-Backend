using System.Text.Json.Serialization;

namespace Milingo.Backend.Models.Yolo;

/// <summary>
/// Maps the JSON response from the YOLO FastAPI microservice.
/// Property names use camelCase to match the YOLO API contract.
/// </summary>
public class YoloDetectionResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("objects")]
    public List<DetectedObject> Objects { get; set; } = new();

    [JsonPropertyName("totalDetected")]
    public int TotalDetected { get; set; }

    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; set; }

    [JsonPropertyName("processingTimeMs")]
    public double ProcessingTimeMs { get; set; }

    /// <summary>
    /// Optional error message when <see cref="Success"/> is false.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
