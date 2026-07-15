namespace Milingo.Backend.Services;

internal enum PaymentOrderAction
{
    Reuse,
    Replace,
    CancelAndReplace,
    RefreshEntitlement
}

internal static class PaymentOrderPolicy
{
    internal static PaymentOrderAction Decide(
        string requestedPlanId,
        string existingPlanId,
        string status,
        DateTime expiresAtUtc,
        DateTime nowUtc)
    {
        if (string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return PaymentOrderAction.RefreshEntitlement;
        }

        var isValidPending = string.Equals(
                status,
                "PENDING",
                StringComparison.OrdinalIgnoreCase)
            && expiresAtUtc.ToUniversalTime() > nowUtc.ToUniversalTime();
        if (!isValidPending)
        {
            return PaymentOrderAction.Replace;
        }

        return string.Equals(
            requestedPlanId,
            existingPlanId,
            StringComparison.OrdinalIgnoreCase)
            ? PaymentOrderAction.Reuse
            : PaymentOrderAction.CancelAndReplace;
    }
}
