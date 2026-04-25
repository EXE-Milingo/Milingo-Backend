using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

// =================================================================
//  REQUEST DTOs
// =================================================================

public class CreateDeckRequest
{
    [Required(ErrorMessage = "Deck name is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 100 characters.")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Description must not exceed 500 characters.")]
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [StringLength(10, ErrorMessage = "Emoji must not exceed 10 characters.")]
    [JsonPropertyName("emoji")]
    public string Emoji { get; set; } = string.Empty;
}

public class UpdateDeckRequest
{
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 100 characters.")]
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [StringLength(500, ErrorMessage = "Description must not exceed 500 characters.")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [StringLength(10, ErrorMessage = "Emoji must not exceed 10 characters.")]
    [JsonPropertyName("emoji")]
    public string? Emoji { get; set; }
}

// =================================================================
//  RESPONSE DTO
// =================================================================

public class DeckResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("emoji")]
    public string Emoji { get; set; } = string.Empty;

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("vocab_count")]
    public int VocabCount { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;
}
