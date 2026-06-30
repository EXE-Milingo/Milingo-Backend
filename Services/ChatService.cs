using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Implements <see cref="IChatService"/> by calling the OpenAI Chat Completions API
/// (POST /v1/chat/completions) with a strongly-scoped system prompt that restricts
/// the AI Tutor to language-learning topics only.
/// </summary>
public sealed class ChatService : IChatService
{
    // ── Configuration ──────────────────────────────────────────────
    private const string ChatCompletionsUrl = "https://api.openai.com/v1/chat/completions";
    private const int MaxHistoryTurns = 20;   // Max message turns kept in context
    private const int MaxOutputTokens = 400;  // Enough for a complete, concise answer
    private const double Temperature = 0.5;   // Focused but not robotic

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<ChatService> _logger;

    // ── Blocked topic keywords (server-side extra guard) ───────────
    private static readonly HashSet<string> _blockedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "suicide", "self-harm", "self harm", "kill myself", "kill yourself",
        "bomb", "terrorist", "terrorism", "explosives", "how to hack",
        "password", "credit card", "api key", "secret key", "private key",
        "sexual", "porn", "nude", "naked",
    };

    public ChatService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ChatService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var apiKey = configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI:ApiKey is not configured.");

        _apiKey = apiKey;
        var model = configuration["OpenAI:Model"];
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
    }

    // ── Public API ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<string> ChatAsync(
        string userId,
        string message,
        IReadOnlyList<ChatMessage> history,
        string targetLanguage,
        string cefrLevel,
        CancellationToken ct = default)
    {
        // 1. Server-side keyword guard (defence-in-depth, system prompt is primary)
        if (ContainsBlockedContent(message))
        {
            _logger.LogWarning(
                "AI Tutor: Blocked message from user {UserId} — matched blocked keyword list.",
                userId);
            return "Xin lỗi, tôi chỉ có thể hỗ trợ các chủ đề về học ngôn ngữ như ngữ pháp, " +
                   "từ vựng, phát âm và luyện tập. Hãy hỏi tôi về {targetLanguage} nhé!";
        }

        // 2. Build the message array: system + trimmed history + new user message
        var messages = BuildMessages(message, history, targetLanguage, cefrLevel);

        // 3. Call OpenAI
        var isReasoningModel = _model.Contains("o1", StringComparison.OrdinalIgnoreCase) || 
                               _model.Contains("o3", StringComparison.OrdinalIgnoreCase) ||
                               _model.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);

        object requestBody;
        if (isReasoningModel)
        {
            requestBody = new
            {
                model = _model,
                messages,
                max_completion_tokens = MaxOutputTokens
            };
        }
        else
        {
            requestBody = new
            {
                model = _model,
                messages,
                max_completion_tokens = MaxOutputTokens,
                temperature = Temperature
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        _logger.LogInformation(
            "AI Tutor: Sending chat request for user {UserId} (lang={Lang}, cefr={Cefr}, historyLen={Len}).",
            userId, targetLanguage, cefrLevel, history.Count);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("AI Tutor: Request cancelled for user {UserId}.", userId);
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "AI Tutor: OpenAI request timed out for user {UserId}.", userId);
            throw new HttpRequestException("AI Tutor timed out. Please try again.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "AI Tutor: OpenAI returned HTTP {StatusCode} for user {UserId}: {Body}",
                (int)response.StatusCode, userId, body);
            throw new HttpRequestException(
                $"AI service returned HTTP {(int)response.StatusCode}.");
        }

        return ExtractReply(body);
    }

    // ── Private Helpers ────────────────────────────────────────────

    /// <summary>Builds the messages array for the Chat Completions API.</summary>
    private static object[] BuildMessages(
        string userMessage,
        IReadOnlyList<ChatMessage> history,
        string targetLanguage,
        string cefrLevel)
    {
        var langName = NormalizeLanguageName(targetLanguage);

        // System prompt: define scope, safety rules, response style
        var systemPrompt = $"""
            You are MiLingo AI Tutor — a friendly, expert language-learning assistant.

            SCOPE (only answer topics within this list):
            - Grammar rules and structures in {langName}
            - Vocabulary: meaning, usage, synonyms, antonyms, collocations
            - Pronunciation and phonetics
            - Example sentences and contextual usage
            - Language learning tips and study strategies
            - Differences between similar words or grammar points
            - Translation questions (between {langName} and Vietnamese)

            HARD RESTRICTIONS — if the user asks about ANY of the following, politely redirect:
            - Violence, weapons, self-harm, suicide, terrorism, extremism
            - Discrimination, hate speech, or offensive content
            - Hacking, cybersecurity, passwords, API keys, or any security topics
            - Personal data, financial information, or legal advice
            - Any topic unrelated to language learning

            RESPONSE RULES:
            - The student is learning {langName} at CEFR level {cefrLevel}. Tailor vocabulary and complexity accordingly.
            - Be concise and efficient. Answer in as few tokens as possible, but NEVER cut a sentence in the middle.
            - Always complete your answer fully — cover the question without unnecessary filler.
            - Use Vietnamese as the primary response language unless the user writes in another language or asks you to respond in {langName}.
            - Format using plain text. Use line breaks to separate sections. Avoid heavy markdown.
            - If you cannot help with a topic, say: "Xin lỗi, tôi chỉ hỗ trợ các chủ đề học ngôn ngữ. Bạn có muốn hỏi về {langName} không?"
            """;

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        // Add trimmed history (max MaxHistoryTurns items, newest last)
        var trimmedHistory = history.Count > MaxHistoryTurns
            ? history.Skip(history.Count - MaxHistoryTurns).ToList()
            : history;

        foreach (var turn in trimmedHistory)
        {
            var role = turn.Role?.ToLowerInvariant() switch
            {
                "assistant" => "assistant",
                _ => "user"
            };
            messages.Add(new { role, content = turn.Content ?? string.Empty });
        }

        // Current user message
        messages.Add(new { role = "user", content = userMessage });

        return [.. messages];
    }

    /// <summary>Checks if the message contains any blocked keywords.</summary>
    private static bool ContainsBlockedContent(string message)
    {
        foreach (var keyword in _blockedKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Extracts the assistant's reply text from the Chat Completions response.</summary>
    private string ExtractReply(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
                throw new InvalidOperationException("OpenAI returned no choices.");

            var content = choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("OpenAI returned empty content.");

            return content.Trim();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogError(ex, "AI Tutor: Failed to parse OpenAI response: {Body}", rawResponse);
            throw new InvalidOperationException("AI returned an unexpected response. Please try again.");
        }
    }

    /// <summary>Maps a BCP-47 language code to a human-readable name.</summary>
    private static string NormalizeLanguageName(string code) =>
        (code?.Trim().ToLowerInvariant().Split(['-', '_'])[0]) switch
        {
            "en" => "English",
            "ja" or "jp" => "Japanese",
            "ko" => "Korean",
            "zh" => "Chinese",
            "fr" => "French",
            "de" => "German",
            "es" => "Spanish",
            "it" => "Italian",
            "vi" => "Vietnamese",
            "th" => "Thai",
            var other => other ?? "English"
        };
}
