using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Abstraction for the Gemini AI image analysis service.
/// </summary>
public interface IGeminiService
{
    /// <summary>
    /// Analyzes an image stream and returns structured vocabulary data.
    /// </summary>
    /// <param name="imageStream">The image file stream.</param>
    /// <param name="mimeType">The MIME type of the image (e.g., "image/jpeg").</param>
    /// <param name="cancellationToken">Token to cancel the request if the client disconnects.</param>
    /// <returns>A <see cref="VocabResponse"/> with the AI-extracted vocabulary.</returns>
    Task<VocabResponse> AnalyzeImageAsync(
        Stream imageStream,
        string mimeType,
        CancellationToken cancellationToken = default);
}
