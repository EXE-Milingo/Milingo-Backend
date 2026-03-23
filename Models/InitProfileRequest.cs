using System.ComponentModel.DataAnnotations;

namespace Milingo.Backend.Models;

/// <summary>
/// Request body for the user profile initialization endpoint.
/// The client sends this after successful Firebase Auth registration.
/// </summary>
public class InitProfileRequest
{
    /// <summary>
    /// The display name chosen by the user during registration.
    /// </summary>
    [Required(ErrorMessage = "DisplayName is required.")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "DisplayName must be between 1 and 50 characters.")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The target language the user wants to learn.
    /// Must be one of the supported languages: English, Japanese, Chinese, Korean,
    /// French, German, Spanish, Italian.
    /// Defaults to "English" if not provided.
    /// </summary>
    [SupportedLanguageValidation]
    public string TargetLanguage { get; set; } = "English";
}

/// <summary>
/// Custom validation attribute that ensures the value is one of the
/// supported languages defined in <see cref="SupportedLanguages"/>.
/// </summary>
public class SupportedLanguageValidationAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is string language && SupportedLanguages.IsValid(language))
        {
            return ValidationResult.Success;
        }

        var allowed = string.Join(", ", SupportedLanguages.All);
        return new ValidationResult(
            $"'{value}' is not a supported language. Allowed values: {allowed}.");
    }
}
