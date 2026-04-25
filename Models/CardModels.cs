using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

// =================================================================
//  REQUEST DTOs
// =================================================================

public class AddCardRequest
{
    [Required(ErrorMessage = "Term is required.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Term must be between 1 and 200 characters.")]
    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [Required(ErrorMessage = "Translation is required.")]
    [StringLength(500, MinimumLength = 1, ErrorMessage = "Translation must be between 1 and 500 characters.")]
    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [StringLength(200)]
    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [StringLength(50)]
    [JsonPropertyName("partOfSpeech")]
    public string PartOfSpeech { get; set; } = string.Empty;

    [Required(ErrorMessage = "Source language code is required.")]
    [StringLength(10, MinimumLength = 2, ErrorMessage = "Source language code must be between 2 and 10 characters.")]
    [JsonPropertyName("sourceLangCode")]
    public string SourceLangCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Target language code is required.")]
    [StringLength(10, MinimumLength = 2, ErrorMessage = "Target language code must be between 2 and 10 characters.")]
    [JsonPropertyName("targetLangCode")]
    public string TargetLangCode { get; set; } = string.Empty;

    [JsonPropertyName("sourceVocabId")]
    public string? SourceVocabId { get; set; }
}

// =================================================================
//  RESPONSE DTOs
// =================================================================

public class CardResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [JsonPropertyName("normalized_term")]
    public string NormalizedTerm { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [JsonPropertyName("part_of_speech")]
    public string PartOfSpeech { get; set; } = string.Empty;

    [JsonPropertyName("source_lang_code")]
    public string SourceLangCode { get; set; } = string.Empty;

    [JsonPropertyName("target_lang_code")]
    public string TargetLangCode { get; set; } = string.Empty;

    [JsonPropertyName("source_vocab_id")]
    public string? SourceVocabId { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;
}

public class SavedStatusResponse
{
    [JsonPropertyName("isSaved")]
    public bool IsSaved { get; set; }

    [JsonPropertyName("deckIds")]
    public List<string> DeckIds { get; set; } = new();
}
