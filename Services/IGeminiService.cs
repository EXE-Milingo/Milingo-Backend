using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Abstraction for the Gemini AI image analysis service.
/// </summary>
public interface IGeminiService
{
    /// <summary>
    /// Analyzes an image stream and returns structured vocabulary data.
    /// Used for the full-image fallback path.
    /// </summary>
    /// <param name="imageStream">The image file stream.</param>
    /// <param name="mimeType">The MIME type of the image (e.g., "image/jpeg").</param>
    /// <param name="cancellationToken">Token to cancel the request if the client disconnects.</param>
    /// <returns>A <see cref="VocabResponse"/> with the AI-extracted vocabulary.</returns>
    Task<VocabResponse> AnalyzeImageAsync(
        Stream imageStream,
        string mimeType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes a base64-encoded image and returns structured vocabulary data.
    /// Used for cropped object images from the YOLO detection pipeline.
    /// </summary>
    /// <param name="base64Image">The base64-encoded image data.</param>
    /// <param name="mimeType">The MIME type (e.g., "image/jpeg").</param>
    /// <param name="detectionLabel">
    /// The YOLO-detected label (e.g. "laptop") used to provide context to Gemini
    /// for more accurate vocabulary extraction.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the request if the client disconnects.</param>
    /// <returns>A <see cref="VocabResponse"/> with the AI-extracted vocabulary.</returns>
    Task<VocabResponse> AnalyzeBase64ImageAsync(
        string base64Image,
        string mimeType,
        string detectionLabel,
        CancellationToken cancellationToken = default);
}
