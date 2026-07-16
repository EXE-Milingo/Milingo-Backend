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
}
