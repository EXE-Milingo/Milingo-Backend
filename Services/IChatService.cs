using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Contract for the AI Tutor conversational chat service.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Sends the user's message (along with conversation history) to the
    /// underlying LLM and returns the assistant's reply.
    /// </summary>
    /// <param name="userId">Firebase UID of the requesting user (for logging/rate-limiting).</param>
    /// <param name="message">The latest user message (already validated ≤ 500 chars).</param>
    /// <param name="history">Previous turns, newest last. The service will take at most the last 20.</param>
    /// <param name="targetLanguage">BCP-47 code of the language the student is learning.</param>
    /// <param name="cefrLevel">Student's CEFR level (A1–C2).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assistant's reply text.</returns>
    Task<string> ChatAsync(
        string userId,
        string message,
        IReadOnlyList<ChatMessage> history,
        string targetLanguage,
        string cefrLevel,
        CancellationToken ct = default);
}
