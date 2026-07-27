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

    internal static List<CardResponse> BuildPool(
        IEnumerable<CardResponse> candidates,
        string requiredLanguage,
        int maxCandidates)
    {
        var usedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(card => MatchesTargetLanguage(
                card.TargetLangCode,
                requiredLanguage))
            .Where(card => !string.IsNullOrWhiteSpace(card.Term))
            .Where(card => usedTerms.Add(card.Term.Trim()))
            .Take(Math.Max(0, maxCandidates))
            .ToList();
    }

    internal static List<CardResponse> SelectDistractors(
        CardResponse correctCard,
        IEnumerable<CardResponse> candidatePool,
        int count)
    {
        var usedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            correctCard.Term.Trim()
        };

        return candidatePool
            .Where(card => !(card.Id == correctCard.Id
                && card.DeckId == correctCard.DeckId))
            .Where(card => MatchesTargetLanguage(
                card.TargetLangCode,
                correctCard.TargetLangCode))
            .Where(card => !string.IsNullOrWhiteSpace(card.Term))
            .Where(card => usedTerms.Add(card.Term.Trim()))
            .OrderBy(_ => Guid.NewGuid())
            .Take(Math.Max(0, count))
            .ToList();
    }
}
