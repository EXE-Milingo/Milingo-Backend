namespace Milingo.Backend.Models;

/// <summary>
/// Defines the languages currently supported by Milingo.
/// Used for server-side validation in InitProfileRequest and
/// exposed via GET /api/v1/users/supported-languages for the mobile client.
/// </summary>
public static class SupportedLanguages
{
    /// <summary>
    /// The set of valid target language codes (case-insensitive lookup).
    /// </summary>
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "English",
        "Japanese",
        "Chinese",
        "Korean",
        "French",
        "German",
        "Spanish",
        "Italian"
    };

    /// <summary>
    /// Detailed metadata for each language — for the mobile client's language picker UI.
    /// </summary>
    public static readonly IReadOnlyList<LanguageInfo> Details = new List<LanguageInfo>
    {
        new("English",  "en", "Tiếng Anh",          "🇺🇸"),
        new("Japanese", "ja", "Tiếng Nhật",          "🇯🇵"),
        new("Chinese",  "zh", "Tiếng Trung",         "🇨🇳"),
        new("Korean",   "ko", "Tiếng Hàn",           "🇰🇷"),
        new("French",   "fr", "Tiếng Pháp",          "🇫🇷"),
        new("German",   "de", "Tiếng Đức",           "🇩🇪"),
        new("Spanish",  "es", "Tiếng Tây Ban Nha",   "🇪🇸"),
        new("Italian",  "it", "Tiếng Ý",             "🇮🇹"),
    };

    public static bool IsValid(string language)
    {
        return All.Contains(language)
            || Details.Any(item => string.Equals(
                item.Code,
                language,
                StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeLanguageCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var cleanInput = input.Trim().Split('-', '_')[0];
        var details = Details.FirstOrDefault(d =>
            string.Equals(d.Code, cleanInput, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Name, cleanInput, StringComparison.OrdinalIgnoreCase));

        return details?.Code.ToLowerInvariant() ?? cleanInput.ToLowerInvariant();
    }
}

/// <summary>
/// Represents a single supported language with metadata for display.
/// </summary>
public record LanguageInfo(
    string Name,
    string Code,
    string NativeName,
    string FlagEmoji);
