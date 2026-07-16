using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

internal static class StudyDistractorPolicy
{
    internal static bool MatchesTargetLanguage(
        string? candidateLanguage,
        string? requiredLanguage)
    {
        var candidate = SupportedLanguages.NormalizeLanguageCode(candidateLanguage);
        var required = SupportedLanguages.NormalizeLanguageCode(requiredLanguage);

        return candidate.Length > 0
            && required.Length > 0
            && string.Equals(candidate, required, StringComparison.OrdinalIgnoreCase);
    }
}
