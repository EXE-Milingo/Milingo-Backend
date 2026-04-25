using System.Text;
using System.Text.Json;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Typed HttpClient service that calls the Google Gemini API to analyze images
/// and return structured vocabulary data.
/// </summary>
public class GeminiService : IGeminiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<GeminiService> _logger;

    public GeminiService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _apiKey = configuration["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Gemini:ApiKey is not configured in appsettings.");
        _model = configuration["Gemini:Model"]
            ?? throw new InvalidOperationException("Gemini:Model is not configured. Set it in .env or appsettings.");
    }

    /// <inheritdoc />
    public async Task<VocabResponse> AnalyzeImageAsync(
        Stream imageStream,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        // Convert the uploaded file stream to Base64 for the Gemini API
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        var base64Image = Convert.ToBase64String(memoryStream.ToArray());

        return await CallGeminiAsync(base64Image, mimeType, prompt: BuildFullImagePrompt(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VocabResponse> AnalyzeBase64ImageAsync(
        string base64Image,
        string mimeType,
        string detectionLabel,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildCroppedObjectPrompt(detectionLabel);
        return await CallGeminiAsync(base64Image, mimeType, prompt, cancellationToken);
    }

    // ═══════════════════════════════════════════════════════════════
    //  PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Shared method that sends a base64 image + text prompt to Gemini
    /// and returns the parsed <see cref="VocabResponse"/>.
    /// </summary>
    private async Task<VocabResponse> CallGeminiAsync(
        string base64Image,
        string mimeType,
        string prompt,
        CancellationToken cancellationToken)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inline_data = new { mime_type = mimeType, data = base64Image }
                        }
                    }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json"
            }
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        _logger.LogInformation("Sending image analysis request to Gemini model '{Model}'...", _model);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(url, jsonContent, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Gemini request cancelled -- client disconnected.");
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Gemini API request timed out.");
            throw new HttpRequestException("The AI service timed out. Please try again with a smaller image.", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini API returned HTTP {StatusCode}: {Body}",
                (int)response.StatusCode, responseBody);
            throw new HttpRequestException(
                $"Gemini API call failed with HTTP {(int)response.StatusCode}.");
        }

        return ParseGeminiResponse(responseBody);
    }

    /// <summary>
    /// Prompt used when sending the full (uncropped) image to Gemini.
    /// This is the original behaviour / fallback path.
    /// </summary>
    private static string BuildFullImagePrompt()
    {
        return "Analyze this image and identify the main object. " +
               "Return a JSON object with exactly these keys: " +
               "'keyword' (the English word for the object), " +
               "'translation' (Vietnamese meaning), " +
               "'pronunciation' (IPA phonetic transcription), " +
               "'example_sentence' (a simple bilingual example sentence). " +
               "Return ONLY the raw JSON object, no markdown formatting.";
    }

    /// <summary>
    /// Prompt used when sending a cropped object image with a YOLO label hint.
    /// The label helps Gemini focus on the correct object.
    /// </summary>
    private static string BuildCroppedObjectPrompt(string detectionLabel)
    {
        return $"This image shows a cropped object detected as '{detectionLabel}'. " +
               "Analyze this object and return a JSON object with exactly these keys: " +
               "'keyword' (the most accurate English word for this specific object), " +
               "'translation' (Vietnamese meaning), " +
               "'pronunciation' (IPA phonetic transcription), " +
               "'example_sentence' (a simple bilingual example sentence using the keyword). " +
               "Return ONLY the raw JSON object, no markdown formatting.";
    }

    /// <summary>
    /// Safely extracts the vocabulary JSON from the Gemini API's response envelope.
    /// </summary>
    private VocabResponse ParseGeminiResponse(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);

            // Navigate into Gemini's response structure:
            // { "candidates": [{ "content": { "parts": [{ "text": "..." }] } }] }
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("Gemini returned no candidates in the response.");
            }

            var textContent = candidates[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(textContent))
            {
                throw new InvalidOperationException("Gemini returned empty text content.");
            }

            // Clean up potential markdown code block wrappers that Gemini sometimes adds
            var cleanJson = textContent
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            var result = JsonSerializer.Deserialize<VocabResponse>(cleanJson);

            if (result is null || string.IsNullOrWhiteSpace(result.Keyword))
            {
                _logger.LogWarning("Gemini returned a parseable but incomplete response: {Json}", cleanJson);
                throw new InvalidOperationException(
                    "The AI could not identify a clear object in the image. Please try a different photo.");
            }

            _logger.LogInformation("Gemini successfully identified keyword: '{Keyword}'", result.Keyword);
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini JSON response: {Response}", rawResponse);
            throw new InvalidOperationException(
                "The AI returned a malformed response. Please try again.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogError(ex, "Unexpected Gemini response structure: {Response}", rawResponse);
            throw new InvalidOperationException(
                "Unexpected response structure from the AI service.", ex);
        }
    }
}