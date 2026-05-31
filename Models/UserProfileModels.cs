using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

public class UserProfileResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("photoUrl")]
    public string? PhotoUrl { get; set; }

    [JsonPropertyName("nativeLanguage")]
    public string? NativeLanguage { get; set; }

    [JsonPropertyName("targetLanguage")]
    public string? TargetLanguage { get; set; }

    [JsonPropertyName("cefrLevel")]
    public string CefrLevel { get; set; } = "A1";

    [JsonPropertyName("isPremium")]
    public bool IsPremium { get; set; }
}

public class UpdateUserProfileRequest
{
    [StringLength(50, MinimumLength = 1, ErrorMessage = "DisplayName must be between 1 and 50 characters.")]
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [StringLength(20, ErrorMessage = "NativeLanguage must be at most 20 characters.")]
    [JsonPropertyName("nativeLanguage")]
    public string? NativeLanguage { get; set; }

    [StringLength(20, ErrorMessage = "TargetLanguage must be at most 20 characters.")]
    [JsonPropertyName("targetLanguage")]
    public string? TargetLanguage { get; set; }

    [StringLength(10, ErrorMessage = "CefrLevel must be at most 10 characters.")]
    [JsonPropertyName("cefrLevel")]
    public string? CefrLevel { get; set; }

    [Url(ErrorMessage = "PhotoUrl must be a valid URL.")]
    [JsonPropertyName("photoUrl")]
    public string? PhotoUrl { get; set; }
}
