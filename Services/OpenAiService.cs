using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Typed HttpClient service that calls the OpenAI Responses API to analyze images
/// and return structured vocabulary data.
/// </summary>
public class OpenAiService : IOpenAiService
{
    private const string ResponsesApiUrl = "https://api.openai.com/v1/responses";

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<OpenAiService> _logger;

    public OpenAiService(HttpClient httpClient, IConfiguration configuration, ILogger<OpenAiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var apiKey = configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI:ApiKey is not configured. Set it in .env or appsettings.");
        }
        _apiKey = apiKey;

        var model = configuration["OpenAI:Model"];
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-5.4-mini" : model;
    }

    /// <inheritdoc />
    public async Task<VocabResponse> AnalyzeImageAsync(
        Stream imageStream,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        var base64Image = Convert.ToBase64String(memoryStream.ToArray());

        return await CallOpenAiAsync(base64Image, mimeType, BuildFullImagePrompt(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VocabResponse> AnalyzeBase64ImageAsync(
        string base64Image,
        string mimeType,
        string detectionLabel,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildCroppedObjectPrompt(detectionLabel);
        return await CallOpenAiAsync(base64Image, mimeType, prompt, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<string>> GenerateDistractorsAsync(
        string term,
        string translation,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            You are a language learning assistant.
            The student is learning {targetLanguage}.
            The correct vocabulary word is: "{term}" (meaning: "{translation}").

            Generate exactly 3 plausible but incorrect alternative terms in {targetLanguage}
            that a student might confuse with "{term}".
            Choose words from a similar category or everyday context.

            Respond only with a JSON array of 3 strings. No explanation. No markdown.
            """;

        var requestBody = new
        {
            model = _model,
            input = prompt,
            max_output_tokens = 150
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Distractor generation cancelled for term '{Term}'.", term);
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Distractor generation timed out for term '{Term}'.", term);
            return new List<string>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Distractor generation failed for term '{Term}'.", term);
            return new List<string>();
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "OpenAI distractor generation returned HTTP {StatusCode} for '{Term}': {Body}",
                (int)response.StatusCode,
                term,
                responseBody);
            return new List<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var textContent = ExtractOutputText(doc.RootElement) ?? "[]";
            var cleanJson = textContent
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty)
                .Trim();

            return (JsonSerializer.Deserialize<List<string>>(cleanJson) ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to parse distractor response for term '{Term}': {Body}", term, responseBody);
            return new List<string>();
        }
    }

    private async Task<VocabResponse> CallOpenAiAsync(
        string base64Image,
        string mimeType,
        string prompt,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = _model,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = prompt },
                        new
                        {
                            type = "input_image",
                            image_url = $"data:{mimeType};base64,{base64Image}",
                            detail = "auto"
                        }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "vocab_response",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            keyword = new { type = "string" },
                            translation = new { type = "string" },
                            pronunciation = new { type = "string" },
                            example_sentence = new { type = "string" }
                        },
                        required = new[]
                        {
                            "keyword",
                            "translation",
                            "pronunciation",
                            "example_sentence"
                        }
                    }
                }
            },
            max_output_tokens = 500
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        _logger.LogInformation("Sending image analysis request to OpenAI model '{Model}'...", _model);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("OpenAI request cancelled: client disconnected.");
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "OpenAI API request timed out.");
            throw new HttpRequestException("The AI service timed out. Please try again with a smaller image.", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI API returned HTTP {StatusCode}: {Body}",
                (int)response.StatusCode, responseBody);
            throw new HttpRequestException(
                $"OpenAI API call failed with HTTP {(int)response.StatusCode}. Response: {responseBody}");
        }

        return ParseOpenAiResponse(responseBody);
    }

    private static string BuildFullImagePrompt()
    {
        return "Analyze this image and identify the main object. " +
               "Return JSON with exactly these keys: " +
               "keyword, translation, pronunciation, example_sentence. " +
               "keyword must be the English word for the object. " +
               "translation must be the Vietnamese meaning. " +
               "pronunciation must be IPA phonetic transcription. " +
               "example_sentence must be a simple bilingual example sentence.";
    }

    private static string BuildCroppedObjectPrompt(string detectionLabel)
    {
        return $"This image shows a cropped object detected as '{detectionLabel}'. " +
               "Analyze this object and return JSON with exactly these keys: " +
               "keyword, translation, pronunciation, example_sentence. " +
               "keyword must be the most accurate English word for this specific object. " +
               "translation must be the Vietnamese meaning. " +
               "pronunciation must be IPA phonetic transcription. " +
               "example_sentence must be a simple bilingual example sentence using the keyword.";
    }

    private VocabResponse ParseOpenAiResponse(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var textContent = ExtractOutputText(doc.RootElement);

            if (string.IsNullOrWhiteSpace(textContent))
            {
                throw new InvalidOperationException("OpenAI returned empty text content.");
            }

            var cleanJson = textContent
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty)
                .Trim();

            var result = JsonSerializer.Deserialize<VocabResponse>(cleanJson);

            if (result is null || string.IsNullOrWhiteSpace(result.Keyword))
            {
                _logger.LogWarning("OpenAI returned a parseable but incomplete response: {Json}", cleanJson);
                throw new InvalidOperationException(
                    "The AI could not identify a clear object in the image. Please try a different photo.");
            }

            _logger.LogInformation("OpenAI successfully identified keyword: '{Keyword}'", result.Keyword);
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse OpenAI JSON response: {Response}", rawResponse);
            throw new InvalidOperationException(
                "The AI returned a malformed response. Please try again.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogError(ex, "Unexpected OpenAI response structure: {Response}", rawResponse);
            throw new InvalidOperationException(
                "Unexpected response structure from the AI service.", ex);
        }
    }

    private static string? ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }
}
