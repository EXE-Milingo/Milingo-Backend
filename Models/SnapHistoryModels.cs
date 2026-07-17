using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

public class SnapHistoryItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("snap_group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SnapGroupId { get; set; }

    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [JsonPropertyName("example_sentence")]
    public string ExampleSentence { get; set; } = string.Empty;

    [JsonPropertyName("related_words")]
    public List<RelatedWordResponse> RelatedWords { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

public class SnapHistoryPage
{
    [JsonPropertyName("items")]
    public List<SnapHistoryItem> Items { get; set; } = new();

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }
}

public sealed record SnapHistoryCursor(DateTime CreatedAt, string DocumentId);
