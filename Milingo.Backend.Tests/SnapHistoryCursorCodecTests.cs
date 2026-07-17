using Milingo.Backend.Models;
using Milingo.Backend.Services;
using Xunit;

namespace Milingo.Backend.Tests;

public class SnapHistoryCursorCodecTests
{
    [Fact]
    public void EncodeAndTryDecode_RoundTripsUtcCursorWithoutPadding()
    {
        var cursor = new SnapHistoryCursor(
            new DateTime(2026, 7, 17, 3, 30, 0, DateTimeKind.Utc),
            "vocab-doc-2");

        var encoded = SnapHistoryCursorCodec.Encode(cursor);

        Assert.DoesNotContain("=", encoded);
        Assert.True(SnapHistoryCursorCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(cursor, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("e30")]
    public void TryDecode_RejectsInvalidCursor(string value)
    {
        Assert.False(SnapHistoryCursorCodec.TryDecode(value, out _));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(21, 20)]
    public void NormalizeLimit_ClampsToSupportedRange(int requested, int expected)
    {
        Assert.Equal(expected, SnapHistoryCursorCodec.NormalizeLimit(requested));
    }
}
