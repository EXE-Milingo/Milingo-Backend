using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Abstraction for the OpenAI image analysis service.
/// </summary>
public interface IOpenAiService
{
    /// <summary>
    /// Analyzes an image stream and returns structured vocabulary data.
    /// Used for the full-image fallback path.
    /// </summary>
    Task<VocabResponse> AnalyzeImageAsync(
        Stream imageStream,
        string mimeType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes a base64-encoded image and returns structured vocabulary data.
    /// Used for cropped object images from the YOLO detection pipeline.
    /// </summary>
    Task<VocabResponse> AnalyzeBase64ImageAsync(
        string base64Image,
        string mimeType,
        string detectionLabel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates plausible but incorrect vocabulary terms for MCQ options.
    /// </summary>
    Task<List<string>> GenerateDistractorsAsync(
        string term,
        string translation,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
