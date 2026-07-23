using Milingo.Backend.Services;
using Xunit;

namespace Milingo.Backend.Tests;

public class PremiumRenewalPolicyTests
{
    private static readonly DateTime NowUtc =
        new(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Expired_user_starts_from_confirmation_time()
    {
        var result = PremiumRenewalPolicy.Decide(
            "PENDING", NowUtc, NowUtc.AddDays(-1), 30);

        Assert.True(result.ShouldApply);
        Assert.Equal(NowUtc.AddDays(30), result.NewExpiryUtc);
    }

    [Fact]
    public void Active_user_keeps_remaining_time()
    {
        var expiry = NowUtc.AddDays(12);
        var result = PremiumRenewalPolicy.Decide(
            "PENDING", NowUtc, expiry, 30);

        Assert.True(result.ShouldApply);
        Assert.Equal(expiry.AddDays(30), result.NewExpiryUtc);
    }

    [Theory]
    [InlineData("PAID")]
    [InlineData("SUCCESS")]
    [InlineData("COMPLETED")]
    public void Paid_order_is_not_reapplied(string status)
    {
        var result = PremiumRenewalPolicy.Decide(
            status, NowUtc, NowUtc.AddDays(12), 30);

        Assert.False(result.ShouldApply);
        Assert.Null(result.NewExpiryUtc);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(365)]
    public void Every_package_adds_its_duration(int durationDays)
    {
        var expiry = NowUtc.AddDays(20);
        var result = PremiumRenewalPolicy.Decide(
            "PENDING", NowUtc, expiry, durationDays);

        Assert.Equal(expiry.AddDays(durationDays), result.NewExpiryUtc);
    }

    [Fact]
    public void Distinct_orders_stack_after_transaction_serialization()
    {
        var first = PremiumRenewalPolicy.Decide(
            "PENDING", NowUtc, NowUtc.AddDays(10), 30);
        var second = PremiumRenewalPolicy.Decide(
            "PENDING", NowUtc, first.NewExpiryUtc, 7);

        Assert.Equal(NowUtc.AddDays(47), second.NewExpiryUtc);
    }

    [Fact]
    public void Non_positive_duration_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PremiumRenewalPolicy.Decide("PENDING", NowUtc, null, 0));
    }
}
