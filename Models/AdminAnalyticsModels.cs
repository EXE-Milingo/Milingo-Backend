namespace Milingo.Backend.Models;

public class AdminAnalyticsOverviewResponse
{
    public AdminAnalyticsTotalsResponse Totals { get; set; } = new();

    public List<AdminAnalyticsGrowthPointResponse> Growth { get; set; } = new();

    public AdminAnalyticsRatingsResponse Ratings { get; set; } = new();

    public List<AdminAnalyticsTransactionResponse> Transactions { get; set; } = new();
}

public class AdminAnalyticsTotalsResponse
{
    public long Revenue { get; set; }

    public int Users { get; set; }

    public int Admins { get; set; }

    public int Customers { get; set; }

    public int Transactions { get; set; }

    public int Reviews { get; set; }

    public double AverageRating { get; set; }
}

public class AdminAnalyticsGrowthPointResponse
{
    public string Period { get; set; } = string.Empty;

    public long Revenue { get; set; }

    public int Users { get; set; }

    public int Transactions { get; set; }

    public int Reviews { get; set; }

    public double AverageRating { get; set; }
}

public class AdminAnalyticsRatingsResponse
{
    public int ThreeStar { get; set; }

    public int FourStar { get; set; }

    public int FiveStar { get; set; }
}

public class AdminAnalyticsTransactionResponse
{
    public string Id { get; set; } = string.Empty;

    public long? OrderCode { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string PlanId { get; set; } = string.Empty;

    public int Amount { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public DateTime? PaidAt { get; set; }
}

public class AdminReviewResponse
{
    public string Id { get; set; } = string.Empty;

    public string ReviewId { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public int Rating { get; set; }

    public string Comment { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public bool Replied { get; set; }
}
