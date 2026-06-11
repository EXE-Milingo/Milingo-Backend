using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

public class SnapQuotaStatus
{
    [JsonPropertyName("isPremium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("dailyLimit")]
    public int DailyLimit { get; set; }

    [JsonPropertyName("usedToday")]
    public int UsedToday { get; set; }

    [JsonPropertyName("remainingToday")]
    public int RemainingToday { get; set; }

    [JsonPropertyName("isLimitReached")]
    public bool IsLimitReached { get; set; }

    [JsonPropertyName("resetAt")]
    public DateTime ResetAt { get; set; }
}

public class SnapSaveResult
{
    public bool IsNew { get; set; }
    public SnapQuotaStatus Quota { get; set; } = new();
}

public class SnapQuotaExceededException : Exception
{
    public SnapQuotaExceededException(SnapQuotaStatus quota)
        : base("Daily free snap limit reached.")
    {
        Quota = quota;
    }

    public SnapQuotaStatus Quota { get; }
}
