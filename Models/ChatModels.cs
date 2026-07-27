namespace Milingo.Backend.Models;

// ──────────────────────────────────────────────────────────────────
//  AI Tutor Chat Models
// ──────────────────────────────────────────────────────────────────

/// <summary>
/// A single turn in a chat conversation.
/// </summary>
/// <param name="Role">"user" or "assistant"</param>
/// <param name="Content">The message text.</param>
public record ChatMessage(string Role, string Content);

/// <summary>
/// Payload sent by the Flutter app to the AI Tutor endpoint.
/// </summary>
public record ChatRequest
{
    /// <summary>The user's latest message (max 500 chars).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Previous turns in the conversation (newest last, max 20 items).</summary>
    public List<ChatMessage> History { get; init; } = [];

    /// <summary>BCP-47 language code the student is learning (e.g. "en", "ja").</summary>
    public string TargetLanguage { get; init; } = "en";

    /// <summary>CEFR level of the student (e.g. "A1", "B2").</summary>
    public string CefrLevel { get; init; } = "A1";
}

/// <summary>
/// Response returned to the Flutter app from the AI Tutor endpoint.
/// </summary>
public record ChatResponse
{
    /// <summary>The AI's reply text.</summary>
    public string Reply { get; init; } = string.Empty;

    /// <summary>Always "assistant".</summary>
    public string Role { get; init; } = "assistant";

    /// <summary>Updated quota info after this message was sent.</summary>
    public ChatQuotaInfo? Quota { get; init; }
}

/// <summary>
/// Daily quota information for the AI Tutor feature (free-tier users only).
/// </summary>
public class ChatQuotaInfo
{
    public bool IsPremium { get; set; }
    public int FreeLimit { get; set; }
    public int UsedToday { get; set; }
    public int RemainingToday { get; set; }
    public bool IsLimitReached { get; set; }
}

/// <summary>
/// Exception thrown when a free-tier user has reached their daily AI Tutor message limit.
/// </summary>
public class ChatQuotaExceededException : Exception
{
    public ChatQuotaExceededException(ChatQuotaInfo quota)
        : base("Daily free AI Tutor message limit reached.")
    {
        Quota = quota;
    }

    public ChatQuotaInfo Quota { get; }
}
