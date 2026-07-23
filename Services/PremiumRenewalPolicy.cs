namespace Milingo.Backend.Services;

internal sealed record PremiumRenewalDecision(
    bool ShouldApply,
    DateTime? NewExpiryUtc);

internal static class PremiumRenewalPolicy
{
    internal static PremiumRenewalDecision Decide(
        string orderStatus,
        DateTime nowUtc,
        DateTime? currentExpiryUtc,
        int durationDays)
    {
        if (durationDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationDays));
        }

        if (IsPaid(orderStatus))
        {
            return new PremiumRenewalDecision(false, null);
        }

        var now = nowUtc.ToUniversalTime();
        var current = currentExpiryUtc?.ToUniversalTime();
        var baseDate = current.HasValue && current.Value > now
            ? current.Value
            : now;

        return new PremiumRenewalDecision(
            true,
            baseDate.AddDays(durationDays));
    }

    private static bool IsPaid(string? status)
    {
        return string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
    }
}
