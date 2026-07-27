using Xunit;

namespace Milingo.Backend.Tests;

public class StudyDistractorSourceTests
{
    [Fact]
    public void Firestore_filters_language_before_the_per_deck_limit()
    {
        var source = File.ReadAllText(RepositoryFile(
            "Services",
            "FirestoreService.cs"));
        var filter = source.IndexOf(
            ".WhereEqualTo(\"target_lang_code\", normalizedLanguage)",
            StringComparison.Ordinal);
        Assert.True(filter >= 0);

        var limit = source.IndexOf(
            ".Limit(perDeckLimit)",
            filter,
            StringComparison.Ordinal);

        Assert.True(limit > filter);
        Assert.Contains("Task.WhenAll(deckTasks)", source);
    }

    [Fact]
    public void Controller_uses_one_session_pool_and_bounded_ai_concurrency()
    {
        var source = File.ReadAllText(RepositoryFile(
            "Controller",
            "StudyController.cs"));

        Assert.Equal(1, Count(source, "GetDistractorPoolAsync("));
        Assert.DoesNotContain("dueCardIds", source);
        Assert.Contains("StudyDistractorPolicy.SelectDistractors(", source);
        Assert.Contains("new SemaphoreSlim(3)", source);
        Assert.Contains("Task.WhenAll(cardTasks)", source);
    }

    private static string RepositoryFile(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            ".."));
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
