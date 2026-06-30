using System.Globalization;
using Google.Cloud.Firestore;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

public class AdminAnalyticsService : IAdminAnalyticsService
{
    private static readonly HashSet<string> PaidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAID",
        "SUCCESS",
        "COMPLETED"
    };

    private readonly FirestoreDb _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminAnalyticsService> _logger;

    public AdminAnalyticsService(
        FirestoreDb db,
        IConfiguration configuration,
        ILogger<AdminAnalyticsService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AdminAnalyticsOverviewResponse> GetOverviewAsync(
        string granularity,
        DateTime? from,
        DateTime? to,
        CancellationToken cancellationToken = default)
    {
        var normalizedGranularity = NormalizeGranularity(granularity);
        var fromUtc = NormalizeStart(from);
        var toUtc = NormalizeEnd(to);
        var adminEmails = GetConfiguredAdminEmails();

        var usersTask = LoadUsersAsync(adminEmails, fromUtc, toUtc, cancellationToken);
        var paidOrdersTask = LoadPaidOrdersAsync(fromUtc, toUtc, cancellationToken);
        var reviewsTask = LoadReviewsAsync(fromUtc, toUtc, cancellationToken);

        await Task.WhenAll(usersTask, paidOrdersTask, reviewsTask);

        var users = usersTask.Result;
        var paidOrders = paidOrdersTask.Result;
        var reviews = reviewsTask.Result;
        var ratingValues = reviews
            .Where(review => review.Rating is >= 1 and <= 5)
            .Select(review => review.Rating)
            .ToList();

        return new AdminAnalyticsOverviewResponse
        {
            Totals = new AdminAnalyticsTotalsResponse
            {
                Revenue = paidOrders.Sum(order => (long)order.Amount),
                Users = users.Count,
                Admins = users.Count(user => user.IsAdmin),
                Customers = users.Count(user => !user.IsAdmin),
                Transactions = paidOrders.Count,
                Reviews = ratingValues.Count,
                AverageRating = RoundAverage(ratingValues)
            },
            Growth = BuildGrowth(normalizedGranularity, users, paidOrders, reviews),
            Ratings = new AdminAnalyticsRatingsResponse
            {
                ThreeStar = reviews.Count(review => review.Rating == 3),
                FourStar = reviews.Count(review => review.Rating == 4),
                FiveStar = reviews.Count(review => review.Rating == 5)
            },
            Transactions = paidOrders
                .OrderByDescending(order => order.PaidAt ?? DateTime.MinValue)
                .Take(12)
                .Select(order => new AdminAnalyticsTransactionResponse
                {
                    Id = order.Id,
                    OrderCode = order.OrderCode,
                    UserId = order.UserId,
                    Email = users.FirstOrDefault(user => user.Id == order.UserId)?.Email ?? string.Empty,
                    PlanId = order.PlanId,
                    Amount = order.Amount,
                    Status = order.Status,
                    Source = order.Source,
                    PaidAt = order.PaidAt
                })
                .ToList()
        };
    }

    private async Task<List<AdminUserRecord>> LoadUsersAsync(
        HashSet<string> adminEmails,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        var snapshot = await _db.Collection("users").GetSnapshotAsync(cancellationToken);
        var users = new List<AdminUserRecord>();

        foreach (var document in snapshot.Documents.Where(doc => doc.Exists))
        {
            var data = document.ToDictionary();
            var createdAt = GetDateTime(data, "created_at") ?? GetDateTime(data, "createdAt");
            if (!IsInRange(createdAt, fromUtc, toUtc, includeWhenMissingDate: true))
                continue;

            var email = GetString(data, "email");
            users.Add(new AdminUserRecord(
                document.Id,
                email,
                IsAdminUser(data, email, adminEmails),
                createdAt));
        }

        return users;
    }

    private async Task<List<AdminPaymentOrderRecord>> LoadPaidOrdersAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        var snapshot = await _db.Collection("payment_orders").GetSnapshotAsync(cancellationToken);
        var paidOrders = new List<AdminPaymentOrderRecord>();

        foreach (var document in snapshot.Documents.Where(doc => doc.Exists))
        {
            var data = document.ToDictionary();
            var status = GetString(data, "status", "PENDING");
            if (!PaidStatuses.Contains(status))
                continue;

            var paidAt = GetDateTime(data, "paidAt")
                ?? GetDateTime(data, "updatedAt")
                ?? GetDateTime(data, "createdAt");

            if (!IsInRange(paidAt, fromUtc, toUtc, includeWhenMissingDate: false))
                continue;

            paidOrders.Add(new AdminPaymentOrderRecord(
                document.Id,
                GetLong(data, "orderCode"),
                GetString(data, "uid"),
                GetString(data, "planId"),
                GetInt(data, "amount"),
                status,
                GetString(data, "source", "payos"),
                paidAt));
        }

        return paidOrders;
    }

    private async Task<List<AdminReviewRecord>> LoadReviewsAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _db.Collection("app_reviews").GetSnapshotAsync(cancellationToken);
            var reviews = new List<AdminReviewRecord>();

            foreach (var document in snapshot.Documents.Where(doc => doc.Exists))
            {
                var data = document.ToDictionary();
                var createdAt = GetDateTime(data, "createdAt") ?? GetDateTime(data, "created_at");
                if (!IsInRange(createdAt, fromUtc, toUtc, includeWhenMissingDate: true))
                    continue;

                reviews.Add(new AdminReviewRecord(
                    document.Id,
                    GetString(data, "uid"),
                    GetInt(data, "rating"),
                    createdAt));
            }

            return reviews;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load app_reviews for admin analytics. Returning empty reviews.");
            return new List<AdminReviewRecord>();
        }
    }

    private static List<AdminAnalyticsGrowthPointResponse> BuildGrowth(
        string granularity,
        List<AdminUserRecord> users,
        List<AdminPaymentOrderRecord> paidOrders,
        List<AdminReviewRecord> reviews)
    {
        var periodKeys = users
            .Select(user => user.CreatedAt)
            .Concat(paidOrders.Select(order => order.PaidAt))
            .Concat(reviews.Select(review => review.CreatedAt))
            .Where(date => date.HasValue)
            .Select(date => GetPeriodKey(date!.Value, granularity))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(period => period, StringComparer.Ordinal)
            .ToList();

        if (periodKeys.Count == 0)
            return new List<AdminAnalyticsGrowthPointResponse>();

        return periodKeys.Select(period =>
        {
            var periodOrders = paidOrders
                .Where(order => order.PaidAt.HasValue && GetPeriodKey(order.PaidAt.Value, granularity) == period)
                .ToList();
            var periodReviews = reviews
                .Where(review => review.CreatedAt.HasValue && GetPeriodKey(review.CreatedAt.Value, granularity) == period)
                .ToList();
            var ratingValues = periodReviews
                .Where(review => review.Rating is >= 1 and <= 5)
                .Select(review => review.Rating)
                .ToList();

            return new AdminAnalyticsGrowthPointResponse
            {
                Period = period,
                Revenue = periodOrders.Sum(order => (long)order.Amount),
                Users = users.Count(user => user.CreatedAt.HasValue && GetPeriodKey(user.CreatedAt.Value, granularity) == period),
                Transactions = periodOrders.Count,
                Reviews = ratingValues.Count,
                AverageRating = RoundAverage(ratingValues)
            };
        }).ToList();
    }

    private HashSet<string> GetConfiguredAdminEmails()
    {
        var configured = _configuration.GetSection("Admin:Emails").Get<string[]>()
            ?? Array.Empty<string>();

        return configured
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(email => email.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAdminUser(
        IReadOnlyDictionary<string, object> data,
        string email,
        HashSet<string> adminEmails)
    {
        if (!string.IsNullOrWhiteSpace(email)
            && adminEmails.Contains(email.Trim().ToLowerInvariant()))
        {
            return true;
        }

        var role = GetString(data, "role");
        if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
            return true;

        return GetBool(data, "isAdmin") || GetBool(data, "admin");
    }

    private static string NormalizeGranularity(string granularity)
    {
        return granularity.Trim().ToLowerInvariant() switch
        {
            "month" => "month",
            "year" => "year",
            _ => "day"
        };
    }

    private static DateTime? NormalizeStart(DateTime? value)
    {
        return value.HasValue
            ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc)
            : null;
    }

    private static DateTime? NormalizeEnd(DateTime? value)
    {
        return value.HasValue
            ? DateTime.SpecifyKind(value.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc)
            : null;
    }

    private static bool IsInRange(
        DateTime? date,
        DateTime? fromUtc,
        DateTime? toUtc,
        bool includeWhenMissingDate)
    {
        if (!date.HasValue)
            return includeWhenMissingDate;

        var utc = date.Value.Kind == DateTimeKind.Utc
            ? date.Value
            : date.Value.ToUniversalTime();

        if (fromUtc.HasValue && utc < fromUtc.Value)
            return false;

        if (toUtc.HasValue && utc > toUtc.Value)
            return false;

        return true;
    }

    private static string GetPeriodKey(DateTime date, string granularity)
    {
        var utc = date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
        return granularity switch
        {
            "year" => utc.ToString("yyyy", CultureInfo.InvariantCulture),
            "month" => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }

    private static double RoundAverage(List<int> values)
    {
        return values.Count == 0
            ? 0
            : Math.Round(values.Average(), 1, MidpointRounding.AwayFromZero);
    }

    private static string GetString(
        IReadOnlyDictionary<string, object> data,
        string fieldName,
        string fallback = "")
    {
        if (!data.TryGetValue(fieldName, out var value) || value is null)
            return fallback;

        return value switch
        {
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? fallback,
            _ => value.ToString() ?? fallback
        };
    }

    private static int GetInt(IReadOnlyDictionary<string, object> data, string fieldName)
    {
        if (!data.TryGetValue(fieldName, out var value) || value is null)
            return 0;

        return value switch
        {
            int number => number,
            long number => number > int.MaxValue
                ? int.MaxValue
                : number < int.MinValue
                    ? int.MinValue
                    : (int)number,
            double number => Convert.ToInt32(number, CultureInfo.InvariantCulture),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static long? GetLong(IReadOnlyDictionary<string, object> data, string fieldName)
    {
        if (!data.TryGetValue(fieldName, out var value) || value is null)
            return null;

        return value switch
        {
            long number => number,
            int number => number,
            double number => Convert.ToInt64(number, CultureInfo.InvariantCulture),
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool GetBool(IReadOnlyDictionary<string, object> data, string fieldName)
    {
        if (!data.TryGetValue(fieldName, out var value) || value is null)
            return false;

        return value switch
        {
            bool boolean => boolean,
            string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static DateTime? GetDateTime(
        IReadOnlyDictionary<string, object> data,
        string fieldName)
    {
        if (!data.TryGetValue(fieldName, out var value) || value is null)
            return null;

        return value switch
        {
            Timestamp timestamp => timestamp.ToDateTime(),
            DateTime dateTime => dateTime.Kind == DateTimeKind.Utc
                ? dateTime
                : dateTime.ToUniversalTime(),
            string text when DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) => parsed,
            _ => null
        };
    }

    private sealed record AdminUserRecord(
        string Id,
        string Email,
        bool IsAdmin,
        DateTime? CreatedAt);

    private sealed record AdminPaymentOrderRecord(
        string Id,
        long? OrderCode,
        string UserId,
        string PlanId,
        int Amount,
        string Status,
        string Source,
        DateTime? PaidAt);

    private sealed record AdminReviewRecord(
        string Id,
        string UserId,
        int Rating,
        DateTime? CreatedAt);
}
