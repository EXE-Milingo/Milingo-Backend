using Milingo.Backend.Services;
using Xunit;

namespace Milingo.Backend.Tests;

public class PaymentOrderPolicyTests
{
    [Fact]
    public void ResolveBankName_keeps_persisted_legal_name()
    {
        Assert.Equal(
            "Persisted Legal Bank",
            PaymentOrderPolicy.ResolveBankName(
                "Persisted Legal Bank",
                "Configured Legal Bank"));
    }

    [Fact]
    public void ResolveBankName_uses_configuration_for_legacy_order()
    {
        Assert.Equal(
            "Configured Legal Bank",
            PaymentOrderPolicy.ResolveBankName(null, "Configured Legal Bank"));
    }

    private static readonly DateTime NowUtc =
        new(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Decide_reuses_valid_pending_order_for_same_plan()
    {
        var result = PaymentOrderPolicy.Decide(
            requestedPlanId: "pro",
            existingPlanId: "pro",
            status: "PENDING",
            expiresAtUtc: NowUtc.AddMinutes(10),
            nowUtc: NowUtc);

        Assert.Equal(PaymentOrderAction.Reuse, result);
    }

    [Theory]
    [InlineData("EXPIRED")]
    [InlineData("CANCELLED")]
    public void Decide_replaces_terminal_unpaid_order(string status)
    {
        var result = PaymentOrderPolicy.Decide(
            "pro",
            "pro",
            status,
            NowUtc.AddMinutes(10),
            NowUtc);

        Assert.Equal(PaymentOrderAction.Replace, result);
    }

    [Fact]
    public void Decide_replaces_pending_order_after_expiry()
    {
        var result = PaymentOrderPolicy.Decide(
            "pro",
            "pro",
            "PENDING",
            NowUtc.AddSeconds(-1),
            NowUtc);

        Assert.Equal(PaymentOrderAction.Replace, result);
    }

    [Fact]
    public void Decide_cancels_pending_order_when_plan_changes()
    {
        var result = PaymentOrderPolicy.Decide(
            "ultra",
            "pro",
            "PENDING",
            NowUtc.AddMinutes(10),
            NowUtc);

        Assert.Equal(PaymentOrderAction.CancelAndReplace, result);
    }

    [Fact]
    public void Decide_refreshes_entitlement_for_paid_order()
    {
        var result = PaymentOrderPolicy.Decide(
            "pro",
            "pro",
            "PAID",
            NowUtc.AddMinutes(10),
            NowUtc);

        Assert.Equal(PaymentOrderAction.RefreshEntitlement, result);
    }
}
