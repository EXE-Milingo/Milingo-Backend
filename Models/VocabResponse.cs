using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

/// <summary>
/// Represents the vocabulary data returned by the Gemini AI analysis.
/// Field names match the Firestore document schema (snake_case).
/// </summary>
public class VocabResponse
{
    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [JsonPropertyName("example_sentence")]
    public string ExampleSentence { get; set; } = string.Empty;
}
