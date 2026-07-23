using Milingo.Backend.Models;
using Milingo.Backend.Services;
using Xunit;

namespace Milingo.Backend.Tests;

public class StudyDistractorPolicyTests
{
    [Theory]
    [InlineData("zh", "zh")]
    [InlineData("Chinese", "zh")]
    [InlineData("zh-CN", "Chinese")]
    public void Matching_target_languages_are_accepted(
        string candidateLanguage,
        string requiredLanguage)
    {
        Assert.True(StudyDistractorPolicy.MatchesTargetLanguage(
            candidateLanguage,
            requiredLanguage));
    }

    [Theory]
    [InlineData("en", "zh")]
    [InlineData("ja", "zh")]
    [InlineData("de", "zh")]
    [InlineData("", "zh")]
    public void Mixed_language_distractors_are_rejected(
        string candidateLanguage,
        string requiredLanguage)
    {
        Assert.False(StudyDistractorPolicy.MatchesTargetLanguage(
            candidateLanguage,
            requiredLanguage));
    }

    [Fact]
    public void BuildPool_keeps_unique_matching_language_cards_across_decks()
    {
        var cards = new[]
        {
            Card("current", "deck-a", "chair", "en"),
            Card("due", "deck-b", "table", "English"),
            Card("duplicate", "deck-c", "TABLE", "en"),
            Card("wrong-language", "deck-c", "椅子", "ja")
        };

        var pool = StudyDistractorPolicy.BuildPool(cards, "en", 50);

        Assert.Equal(2, pool.Count);
        Assert.Contains(pool, card => card.Id == "current");
        Assert.Contains(pool, card => card.Id == "due");
    }

    [Fact]
    public void SelectDistractors_reuses_same_session_cards_but_excludes_current_term()
    {
        var correct = Card("current", "deck-a", "chair", "en");
        var pool = new[]
        {
            correct,
            Card("same-term", "deck-b", "CHAIR", "en"),
            Card("one", "deck-a", "table", "en"),
            Card("two", "deck-b", "lamp", "English"),
            Card("three", "deck-c", "sofa", "en"),
            Card("wrong-language", "deck-c", "椅子", "ja")
        };

        var distractors =
            StudyDistractorPolicy.SelectDistractors(correct, pool, 3);

        Assert.Equal(3, distractors.Count);
        Assert.All(distractors, card =>
            Assert.True(StudyDistractorPolicy.MatchesTargetLanguage(
                card.TargetLangCode,
                "en")));
        Assert.DoesNotContain(distractors, card =>
            string.Equals(
                card.Term,
                "chair",
                StringComparison.OrdinalIgnoreCase));
    }

    private static CardResponse Card(
        string id,
        string deckId,
        string term,
        string language) => new()
    {
        Id = id,
        DeckId = deckId,
        Term = term,
        TargetLangCode = language
    };
}
