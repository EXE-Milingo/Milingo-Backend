using System.Text.Json;
using Milingo.Backend.Models.Yolo;

namespace Milingo.Backend.Services;

/// <summary>
/// Typed HttpClient service that calls the YOLO FastAPI microservice
/// to detect and crop objects from an image.
///
/// If the YOLO service is unreachable, times out, or returns an error,
/// this service returns <c>null</c> so the caller can fall back to
/// sending the full image to Gemini.
/// </summary>
public class YoloService : IYoloService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<YoloService> _logger;

    public YoloService(HttpClient httpClient, ILogger<YoloService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<YoloDetectionResponse?> DetectObjectsAsync(
        Stream imageStream,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Build multipart/form-data request
            using var content = new MultipartFormDataContent();
            using var streamContent = new StreamContent(imageStream);
            streamContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);

            content.Add(streamContent, "file", fileName);

            _logger.LogInformation(
                "Sending image '{FileName}' to YOLO service for detection...", fileName);

            var response = await _httpClient.PostAsync(
                "/detect?confidence_threshold=0.5&max_objects=3",
                content,
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "YOLO service returned HTTP {StatusCode}: {Body}",
                    (int)response.StatusCode, json);
                return null; // -> fallback
            }

            var result = JsonSerializer.Deserialize<YoloDetectionResponse>(json);

            if (result is null || !result.Success)
            {
                _logger.LogWarning(
                    "YOLO service returned success=false: {Error}",
                    result?.Error ?? "(null response)");
                return null; // -> fallback
            }

            _logger.LogInformation(
                "YOLO detected {Count} objects in {Time}ms",
                result.ReturnedCount, result.ProcessingTimeMs);

            return result;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected — propagate
            throw;
        }
        catch (Exception ex)
        {
            // Any other failure (timeout, DNS, network, deserialization) -> fallback
            _logger.LogWarning(ex, "YOLO service call failed -- falling back to full-image Gemini.");
            return null;
        }
    }
}
