using Milingo.Backend.Models.Yolo;

namespace Milingo.Backend.Services;

/// <summary>
/// Abstraction for the YOLO object detection microservice.
/// </summary>
public interface IYoloService
{
    /// <summary>
    /// Sends an image to the YOLO microservice for object detection.
    /// </summary>
    /// <param name="imageStream">The image file stream.</param>
    /// <param name="fileName">Original file name (for multipart form).</param>
    /// <param name="mimeType">The MIME type of the image.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>
    /// The YOLO detection response, or <c>null</c> if the service
    /// is unavailable or returned an error (triggers fallback).
    /// </returns>
    Task<YoloDetectionResponse?> DetectObjectsAsync(
        Stream imageStream,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default);
}
