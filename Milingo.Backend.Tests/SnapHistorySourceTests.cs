using Xunit;

namespace Milingo.Backend.Tests;

public class SnapHistorySourceTests
{
    [Fact]
    public void History_query_is_user_scoped_stably_ordered_and_bounded()
    {
        var source = File.ReadAllText(RepositoryFile("Services", "FirestoreService.cs"));

        Assert.Contains("Collection(\"users\").Document(userId)", source);
        Assert.Contains(".Collection(\"vocabularies\")", source);
        Assert.Contains(".OrderByDescending(\"created_at\")", source);
        Assert.Contains(".OrderByDescending(FieldPath.DocumentId)", source);
        Assert.Contains(".StartAfter(", source);
        Assert.Contains(".Limit(pageSize + 1)", source);
    }

    [Fact]
    public void Controller_reads_uid_from_claims_and_rejects_bad_cursor()
    {
        var source = File.ReadAllText(RepositoryFile("Controller", "SnapController.cs"));

        Assert.Contains("[HttpGet(\"history\")]", source);
        Assert.Contains("User.GetFirebaseUid()", source);
        Assert.Contains("SnapHistoryCursorCodec.TryDecode", source);
        Assert.Contains("return BadRequest", source);
        Assert.DoesNotContain("string userId, int limit", source);
    }

    private static string RepositoryFile(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }
}
