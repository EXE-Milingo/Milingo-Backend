using Xunit;

namespace Milingo.Backend.Tests;

public class PaymentServiceRenewalSourceTests
{
    [Fact]
    public void PayOS_paid_paths_share_one_atomic_entitlement_operation()
    {
        var source = File.ReadAllText(RepositoryFile("Services", "PaymentService.cs"));

        Assert.Equal(3, Count(source, "ApplyPaidPayOSOrderAsync("));
        Assert.Contains("RunTransactionAsync", source);
        Assert.Contains("entitlementAppliedAt", source);
        Assert.Contains("grantedPremiumExpiresAt", source);
        Assert.Equal(1, Count(source, "SetPremiumAsync("));
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

    private static int Count(string source, string value)
    {
        return source.Split(value, StringSplitOptions.None).Length - 1;
    }
}
