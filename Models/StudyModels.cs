using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

public class StudyCard
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("deck_id")]
    public string DeckId { get; set; } = string.Empty;

    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [JsonPropertyName("example_sentence")]
    public string ExampleSentence { get; set; } = string.Empty;

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("srs_state")]
    public string SrsState { get; set; } = "new";

    [JsonPropertyName("srs_repetitions")]
    public int SrsRepetitions { get; set; }

    [JsonPropertyName("srs_interval_days")]
    public int SrsIntervalDays { get; set; }

    [JsonPropertyName("suggested_mode")]
    public string SuggestedMode { get; set; } = "flashcard";

    [JsonPropertyName("options")]
    public List<StudyOption>? Options { get; set; }
}

public class StudyOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [JsonPropertyName("is_correct")]
    public bool IsCorrect { get; set; }
}

public class StudySessionResponse
{
    [JsonPropertyName("deck_id")]
    public string DeckId { get; set; } = string.Empty;

    [JsonPropertyName("deck_name")]
    public string DeckName { get; set; } = string.Empty;

    [JsonPropertyName("cards")]
    public List<StudyCard> Cards { get; set; } = new();

    [JsonPropertyName("total_due")]
    public int TotalDue { get; set; }

    [JsonPropertyName("flashcard_count")]
    public int FlashcardCount { get; set; }

    [JsonPropertyName("mcq_count")]
    public int McqCount { get; set; }
}

public class SubmitStudyAnswerRequest
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("deck_id")]
    public string DeckId { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("quality")]
    public int? Quality { get; set; }

    [JsonPropertyName("is_correct")]
    public bool? IsCorrect { get; set; }
}

public class SubmitStudyAnswerResponse
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("quality_applied")]
    public int QualityApplied { get; set; }

    [JsonPropertyName("new_srs_state")]
    public string NewSrsState { get; set; } = string.Empty;

    [JsonPropertyName("new_interval_days")]
    public int NewIntervalDays { get; set; }

    [JsonPropertyName("next_review_at")]
    public string NextReviewAt { get; set; } = string.Empty;

    [JsonPropertyName("coins_awarded")]
    public int CoinsAwarded { get; set; }
}

public class DeckStudyStats
{
    [JsonPropertyName("deck_id")]
    public string DeckId { get; set; } = string.Empty;

    [JsonPropertyName("new_count")]
    public int NewCount { get; set; }

    [JsonPropertyName("learning_count")]
    public int LearningCount { get; set; }

    [JsonPropertyName("review_count")]
    public int ReviewCount { get; set; }

    [JsonPropertyName("mastered_count")]
    public int MasteredCount { get; set; }

    [JsonPropertyName("due_today")]
    public int DueToday { get; set; }

    [JsonPropertyName("total_cards")]
    public int TotalCards { get; set; }
}
