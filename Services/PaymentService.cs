using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Hosting;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

public class PaymentService : IPaymentService
{
    private const string PayOSPaymentRequestsUrl = "https://api-merchant.payos.vn/v2/payment-requests";
    private const string AndroidPublisherScope = "https://www.googleapis.com/auth/androidpublisher";

    private readonly HttpClient _httpClient;
    private readonly IFirestoreService _firestoreService;
    private readonly FirestoreDb _db;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PaymentService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public PaymentService(
        HttpClient httpClient,
        IFirestoreService firestoreService,
        FirestoreDb db,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<PaymentService> logger)
    {
        _httpClient = httpClient;
        _firestoreService = firestoreService;
        _db = db;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CreatePayOSOrderResponse> CreatePayOSOrderAsync(
        string uid,
        string planId,
        string returnUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        var clientId = GetRequiredConfig("PayOS:ClientId");
        var apiKey = GetRequiredConfig("PayOS:ApiKey");
        var checksumKey = GetRequiredConfig("PayOS:ChecksumKey");
        var amount = GetPlanAmount(planId);
        var normalizedPlanId = NormalizePlanId(planId);
        var orderCode = GenerateOrderCode();
        var premiumExpiresAt = GetPremiumExpiresAt(normalizedPlanId);
        var description = BuildPayOSDescription(normalizedPlanId);

        var signatureFields = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            { "amount", amount },
            { "cancelUrl", cancelUrl },
            { "description", description },
            { "orderCode", orderCode },
            { "returnUrl", returnUrl }
        };

        var requestBody = new Dictionary<string, object?>(signatureFields)
        {
            { "signature", CreateHmacSignature(signatureFields, checksumKey) }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, PayOSPaymentRequestsUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, _jsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("x-client-id", clientId);
        request.Headers.Add("x-api-key", apiKey);

        _logger.LogInformation(
            "Creating PayOS order {OrderCode} for user '{Uid}', plan '{PlanId}', amount {Amount}.",
            orderCode, uid, normalizedPlanId, amount);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "PayOS create order failed. HTTP {StatusCode}: {Body}",
                (int)response.StatusCode, responseBody);
            throw new HttpRequestException($"PayOS create order failed with HTTP {(int)response.StatusCode}.");
        }

        var result = ParsePayOSCreateOrderResponse(responseBody);
        if (result.OrderCode == 0)
        {
            result.OrderCode = orderCode;
        }

        await SavePayOSOrderAsync(
            uid,
            normalizedPlanId,
            amount,
            result.OrderCode,
            result.PaymentLinkId,
            result.CheckoutUrl,
            premiumExpiresAt,
            cancellationToken);

        _logger.LogInformation(
            "Created PayOS order {OrderCode} for user '{Uid}'. PaymentLinkId='{PaymentLinkId}'.",
            result.OrderCode, uid, result.PaymentLinkId);

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> HandlePayOSWebhookAsync(
        PayOSWebhookPayload payload,
        string signature,
        CancellationToken cancellationToken = default)
    {
        var checksumKey = GetRequiredConfig("PayOS:ChecksumKey");

        if (string.IsNullOrWhiteSpace(signature))
        {
            _logger.LogWarning("PayOS webhook rejected: missing signature.");
            return false;
        }

        if (!VerifyPayOSSignature(payload.Data, signature, checksumKey))
        {
            _logger.LogWarning(
                "PayOS webhook rejected: invalid signature for order {OrderCode}.",
                payload.Data.OrderCode);
            return false;
        }

        _logger.LogInformation(
            "PayOS webhook verified for order {OrderCode}. Code='{Code}', Status='{Status}'.",
            payload.Data.OrderCode, payload.Code, payload.Data.Status);

        if (!string.Equals(payload.Code, "00", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(payload.Data.Status, "PAID", StringComparison.OrdinalIgnoreCase))
        {
            await MarkPayOSOrderStatusAsync(
                payload.Data.OrderCode,
                string.IsNullOrWhiteSpace(payload.Data.Status) ? payload.Code : payload.Data.Status,
                cancellationToken);
            return true;
        }

        var order = await GetPayOSOrderAsync(payload.Data.OrderCode, cancellationToken);
        if (order is null)
        {
            _logger.LogWarning(
                "PayOS webhook paid but order {OrderCode} not found in Firestore.",
                payload.Data.OrderCode);
            return false;
        }

        if (order.Amount != payload.Data.Amount)
        {
            _logger.LogWarning(
                "PayOS webhook amount mismatch for order {OrderCode}. Expected {Expected}, got {Actual}.",
                payload.Data.OrderCode, order.Amount, payload.Data.Amount);
            return false;
        }

        await _firestoreService.SetPremiumAsync(
            order.Uid,
            order.PremiumExpiresAt,
            "payos",
            cancellationToken);

        await MarkPayOSOrderStatusAsync(payload.Data.OrderCode, "PAID", cancellationToken);

        _logger.LogInformation(
            "PayOS order {OrderCode} marked paid. Premium granted to user '{Uid}' until {ExpiresAt:o}.",
            payload.Data.OrderCode, order.Uid, order.PremiumExpiresAt);

        return true;
    }

    /// <inheritdoc />
    public async Task<VerifyGooglePurchaseResponse> VerifyGooglePurchaseAsync(
        string uid,
        string purchaseToken,
        string productId,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await GetGoogleAccessTokenAsync(cancellationToken);
        var url =
            "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/" +
            $"{Uri.EscapeDataString(packageName)}/purchases/subscriptions/" +
            $"{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(purchaseToken)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        _logger.LogInformation(
            "Verifying Google Play purchase for user '{Uid}', package '{PackageName}', product '{ProductId}'.",
            uid, packageName, productId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Google Play purchase verify failed. HTTP {StatusCode}: {Body}",
                (int)response.StatusCode, responseBody);
            return new VerifyGooglePurchaseResponse { IsValid = false };
        }

        var verification = ParseGoogleSubscriptionResponse(responseBody);
        if (verification.IsValid && verification.ExpiresAt.HasValue)
        {
            await _firestoreService.SetPremiumAsync(
                uid,
                verification.ExpiresAt.Value,
                "google_play",
                cancellationToken);

            _logger.LogInformation(
                "Google Play purchase valid for user '{Uid}' until {ExpiresAt:o}.",
                uid, verification.ExpiresAt.Value);
        }
        else
        {
            _logger.LogWarning(
                "Google Play purchase invalid for user '{Uid}'. ExpiresAt={ExpiresAt:o}.",
                uid, verification.ExpiresAt);
        }

        return verification;
    }

    /// <inheritdoc />
    public async Task<SubscriptionOverviewResponse> GetSubscriptionOverviewAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        var status = await _firestoreService.GetPremiumStatusAsync(uid, cancellationToken);
        var orders = await GetUserPaymentOrdersAsync(uid, cancellationToken);
        var paidOrders = orders
            .Where(order => IsPaidStatus(order.Status))
            .OrderByDescending(GetOrderSortDate)
            .ToList();
        var latestPaidOrder = paidOrders.FirstOrDefault();
        var activePlanId = status.IsPremium ? latestPaidOrder?.PlanId : null;
        var startedAt = latestPaidOrder?.PaidAt ?? latestPaidOrder?.CreatedAt;
        var memberSince = GetMemberSince(paidOrders);
        var lastPaymentAmount = latestPaidOrder?.Amount ?? 0;

        return new SubscriptionOverviewResponse
        {
            IsPremium = status.IsPremium,
            PlanId = activePlanId,
            PlanName = status.IsPremium ? GetPlanDisplayName(activePlanId) : "Gói miễn phí",
            ExpiresAt = status.ExpiresAt,
            Source = status.Source,
            RemainingDays = GetRemainingDays(status.ExpiresAt),
            StartedAt = startedAt,
            MemberSince = memberSince,
            LastPaymentAt = latestPaidOrder?.PaidAt ?? latestPaidOrder?.UpdatedAt,
            LastPaymentAmount = lastPaymentAmount,
            NextPaymentAmount = lastPaymentAmount > 0
                ? lastPaymentAmount
                : TryGetConfiguredPlanAmount(activePlanId) ?? 0,
            MonthlyProgressPercent = ComputeProgressPercent(startedAt, status.ExpiresAt),
            Benefits = GetPlanBenefits(activePlanId)
        };
    }

    /// <inheritdoc />
    public async Task<PaymentTransactionHistoryResponse> GetPaymentTransactionHistoryAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        var orders = await GetUserPaymentOrdersAsync(uid, cancellationToken);
        var now = DateTime.UtcNow;
        var yearStart = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextYearStart = yearStart.AddYears(1);

        var transactions = orders
            .OrderByDescending(GetOrderSortDate)
            .Select(MapTransaction)
            .ToList();

        var totalSpentThisYear = orders
            .Where(order => IsPaidStatus(order.Status))
            .Where(order =>
            {
                var paidAt = order.PaidAt ?? order.UpdatedAt ?? order.CreatedAt;
                return paidAt.HasValue
                    && paidAt.Value >= yearStart
                    && paidAt.Value < nextYearStart;
            })
            .Sum(order => order.Amount);

        return new PaymentTransactionHistoryResponse
        {
            TotalSpentThisYear = totalSpentThisYear,
            Transactions = transactions
        };
    }

    private CreatePayOSOrderResponse ParsePayOSCreateOrderResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeElement)
            ? codeElement.GetString()
            : null;

        if (!string.Equals(code, "00", StringComparison.OrdinalIgnoreCase))
        {
            var desc = root.TryGetProperty("desc", out var descElement)
                ? descElement.GetString()
                : "Unknown PayOS error.";
            _logger.LogWarning("PayOS create order returned code '{Code}': {Desc}", code, desc);
            throw new InvalidOperationException("PayOS rejected the payment order.");
        }

        var data = root.GetProperty("data");
        return new CreatePayOSOrderResponse
        {
            CheckoutUrl = data.GetProperty("checkoutUrl").GetString() ?? string.Empty,
            OrderCode = data.GetProperty("orderCode").GetInt64(),
            PaymentLinkId = data.GetProperty("paymentLinkId").GetString() ?? string.Empty
        };
    }

    private VerifyGooglePurchaseResponse ParseGoogleSubscriptionResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var acknowledgementState = root.TryGetProperty("acknowledgementState", out var ackElement)
            && ackElement.TryGetInt32(out var ack)
                ? ack
                : 0;
        var expiresAt = TryReadExpiryTime(root);
        var isValid = acknowledgementState == 1
            && expiresAt.HasValue
            && expiresAt.Value > DateTime.UtcNow;

        return new VerifyGooglePurchaseResponse
        {
            IsValid = isValid,
            ExpiresAt = expiresAt
        };
    }

    private static DateTime? TryReadExpiryTime(JsonElement root)
    {
        if (!root.TryGetProperty("expiryTimeMillis", out var expiryElement))
            return null;

        var raw = expiryElement.ValueKind == JsonValueKind.String
            ? expiryElement.GetString()
            : expiryElement.GetRawText();

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
            return null;

        return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
    }

    private async Task<string> GetGoogleAccessTokenAsync(CancellationToken cancellationToken)
    {
        var serviceAccountJson = GetGoogleServiceAccountJson();
        var credential = CredentialFactory
            .FromJson<ServiceAccountCredential>(serviceAccountJson)
            .ToGoogleCredential()
            .CreateScoped(AndroidPublisherScope);

        return await ((ITokenAccess)credential)
            .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
    }

    private string GetGoogleServiceAccountJson()
    {
        var configuredValue = GetRequiredConfig("Google:ServiceAccountJson");
        if (configuredValue.TrimStart().StartsWith('{'))
        {
            return configuredValue;
        }

        var path = Path.IsPathRooted(configuredValue)
            ? configuredValue
            : Path.Combine(_environment.ContentRootPath, configuredValue);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Google service account JSON not found: {path}");
        }

        return File.ReadAllText(path);
    }

    private async Task SavePayOSOrderAsync(
        string uid,
        string planId,
        int amount,
        long orderCode,
        string paymentLinkId,
        string checkoutUrl,
        DateTime premiumExpiresAt,
        CancellationToken cancellationToken)
    {
        var orderRef = _db.Collection("payment_orders")
            .Document(orderCode.ToString(CultureInfo.InvariantCulture));
        var expiresAtUtc = DateTime.SpecifyKind(premiumExpiresAt.ToUniversalTime(), DateTimeKind.Utc);

        await orderRef.SetAsync(new Dictionary<string, object>
        {
            { "uid", uid },
            { "planId", planId },
            { "amount", amount },
            { "orderCode", orderCode },
            { "paymentLinkId", paymentLinkId },
            { "checkoutUrl", checkoutUrl },
            { "status", "PENDING" },
            { "premiumExpiresAt", Timestamp.FromDateTime(expiresAtUtc) },
            { "source", "payos" },
            { "createdAt", FieldValue.ServerTimestamp },
            { "updatedAt", FieldValue.ServerTimestamp }
        }, SetOptions.MergeAll, cancellationToken);
    }

    private async Task<PayOSOrderRecord?> GetPayOSOrderAsync(
        long orderCode,
        CancellationToken cancellationToken)
    {
        var orderRef = _db.Collection("payment_orders")
            .Document(orderCode.ToString(CultureInfo.InvariantCulture));
        var snapshot = await orderRef.GetSnapshotAsync(cancellationToken);

        if (!snapshot.Exists)
            return null;

        return new PayOSOrderRecord(
            snapshot.GetValue<string>("uid"),
            snapshot.ContainsField("amount") ? snapshot.GetValue<int>("amount") : 0,
            snapshot.ContainsField("premiumExpiresAt")
                ? snapshot.GetValue<Timestamp>("premiumExpiresAt").ToDateTime()
                : GetPremiumExpiresAt(snapshot.ContainsField("planId")
                    ? snapshot.GetValue<string>("planId")
                    : "monthly"));
    }

    private async Task MarkPayOSOrderStatusAsync(
        long orderCode,
        string status,
        CancellationToken cancellationToken)
    {
        var orderRef = _db.Collection("payment_orders")
            .Document(orderCode.ToString(CultureInfo.InvariantCulture));
        var updates = new Dictionary<string, object>
        {
            { "status", status },
            { "updatedAt", FieldValue.ServerTimestamp }
        };

        if (IsPaidStatus(status))
        {
            updates["paidAt"] = FieldValue.ServerTimestamp;
        }

        await orderRef.SetAsync(updates, SetOptions.MergeAll, cancellationToken);
    }

    private bool VerifyPayOSSignature(PayOSWebhookData data, string signature, string checksumKey)
    {
        var fields = BuildPayOSWebhookSignatureFields(data);
        var expected = CreateHmacSignature(fields, checksumKey);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    private static SortedDictionary<string, object?> BuildPayOSWebhookSignatureFields(PayOSWebhookData data)
    {
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            { "amount", data.Amount },
            { "orderCode", data.OrderCode },
            { "paymentLinkId", data.PaymentLinkId }
        };

        if (!string.IsNullOrWhiteSpace(data.Status))
        {
            fields["status"] = data.Status;
        }

        if (data.ExtraData is not null)
        {
            foreach (var item in data.ExtraData)
            {
                if (string.Equals(item.Key, "signature", StringComparison.OrdinalIgnoreCase)
                    || fields.ContainsKey(item.Key))
                {
                    continue;
                }

                fields[item.Key] = ToPayOSSignatureValue(item.Value);
            }
        }

        return fields;
    }

    private static string CreateHmacSignature(
        IReadOnlyDictionary<string, object?> fields,
        string checksumKey)
    {
        var data = string.Join("&", fields.Select(f => $"{f.Key}={ToSignatureString(f.Value)}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(checksumKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private static string ToSignatureString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            JsonElement json => ToPayOSSignatureValue(json),
            string text when text is "undefined" or "null" => string.Empty,
            string text => text,
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(value)
        };
    }

    private static string ToPayOSSignatureValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => JsonSerializer.Serialize(value)
        };
    }

    private string GetRequiredConfig(string key)
    {
        var value = _configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is not configured.");
        }

        return value;
    }

    private int GetPlanAmount(string planId)
    {
        var sectionName = GetPlanSectionName(planId);
        var amount = _configuration.GetValue<int?>($"Subscriptions:{sectionName}:Amount");

        if (amount is null or <= 0)
        {
            throw new InvalidOperationException($"Subscription plan '{planId}' is not configured.");
        }

        return amount.Value;
    }

    private int? TryGetConfiguredPlanAmount(string? planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return null;

        try
        {
            return GetPlanAmount(planId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private DateTime GetPremiumExpiresAt(string planId)
    {
        var sectionName = GetPlanSectionName(planId);
        var durationDays = _configuration.GetValue<int?>($"Subscriptions:{sectionName}:DurationDays") ?? 30;

        return DateTime.UtcNow.AddDays(durationDays);
    }

    private static string GetPlanSectionName(string planId)
    {
        var normalizedPlanId = NormalizePlanId(planId);
        return string.Join(
            "_",
            normalizedPlanId
                .Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part)));
    }

    private static string NormalizePlanId(string planId)
    {
        return string.IsNullOrWhiteSpace(planId)
            ? "monthly"
            : planId.Trim().ToLowerInvariant();
    }

    private static string BuildPayOSDescription(string planId)
    {
        var description = $"Milingo {planId}";
        var sanitized = new string(description
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ')
            .ToArray())
            .Trim();

        return sanitized[..Math.Min(sanitized.Length, 25)];
    }

    private async Task<List<PaymentOrderDocument>> GetUserPaymentOrdersAsync(
        string uid,
        CancellationToken cancellationToken)
    {
        var snapshot = await _db.Collection("payment_orders")
            .WhereEqualTo("uid", uid)
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Documents
            .Where(document => document.Exists)
            .Select(MapPaymentOrder)
            .ToList();
    }

    private static PaymentOrderDocument MapPaymentOrder(DocumentSnapshot snapshot)
    {
        var data = snapshot.ToDictionary();
        var status = GetString(data, "status", "PENDING");
        var updatedAt = GetDateTime(data, "updatedAt");
        var createdAt = GetDateTime(data, "createdAt");
        var paidAt = GetDateTime(data, "paidAt");

        if (paidAt is null && IsPaidStatus(status))
        {
            paidAt = updatedAt ?? createdAt;
        }

        return new PaymentOrderDocument(
            snapshot.Id,
            GetLong(data, "orderCode"),
            NormalizePlanId(GetString(data, "planId", "monthly")),
            GetInt(data, "amount"),
            status,
            GetString(data, "source", "payos"),
            GetString(data, "paymentLinkId", string.Empty),
            GetString(data, "checkoutUrl", string.Empty),
            createdAt,
            updatedAt,
            paidAt,
            GetDateTime(data, "premiumExpiresAt"));
    }

    private static PaymentTransactionResponse MapTransaction(PaymentOrderDocument order)
    {
        return new PaymentTransactionResponse
        {
            Id = order.Id,
            OrderCode = order.OrderCode,
            PlanId = order.PlanId,
            PlanName = GetPlanDisplayName(order.PlanId),
            Amount = order.Amount,
            Status = order.Status,
            StatusLabel = GetStatusLabel(order.Status),
            Source = order.Source,
            PaymentMethodLabel = GetPaymentMethodLabel(order.Source),
            PaymentLinkId = string.IsNullOrWhiteSpace(order.PaymentLinkId) ? null : order.PaymentLinkId,
            CheckoutUrl = string.IsNullOrWhiteSpace(order.CheckoutUrl) ? null : order.CheckoutUrl,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            PaidAt = order.PaidAt
        };
    }

    private static DateTime GetOrderSortDate(PaymentOrderDocument order)
    {
        return order.PaidAt ?? order.UpdatedAt ?? order.CreatedAt ?? DateTime.MinValue;
    }

    private static DateTime? GetMemberSince(IEnumerable<PaymentOrderDocument> paidOrders)
    {
        DateTime? memberSince = null;

        foreach (var order in paidOrders)
        {
            var date = order.PaidAt ?? order.CreatedAt;
            if (date is null)
                continue;

            if (memberSince is null || date.Value < memberSince.Value)
            {
                memberSince = date.Value;
            }
        }

        return memberSince;
    }

    private static int? GetRemainingDays(DateTime? expiresAt)
    {
        if (!expiresAt.HasValue)
            return null;

        var remaining = expiresAt.Value.ToUniversalTime() - DateTime.UtcNow;
        return Math.Max(0, (int)Math.Ceiling(remaining.TotalDays));
    }

    private static int ComputeProgressPercent(DateTime? startedAt, DateTime? expiresAt)
    {
        if (!startedAt.HasValue || !expiresAt.HasValue)
            return 0;

        var start = startedAt.Value.ToUniversalTime();
        var end = expiresAt.Value.ToUniversalTime();
        var totalSeconds = (end - start).TotalSeconds;

        if (totalSeconds <= 0)
            return 100;

        var elapsedSeconds = (DateTime.UtcNow - start).TotalSeconds;
        return Math.Clamp((int)Math.Round(elapsedSeconds / totalSeconds * 100), 0, 100);
    }

    private static bool IsPaidStatus(string? status)
    {
        return string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPlanDisplayName(string? planId)
    {
        return NormalizePlanId(planId ?? string.Empty) switch
        {
            "plus_monthly" => "Gói Plus",
            "plus_yearly" => "Gói Plus Năm",
            "pro_monthly" => "Gói Pro",
            "pro_yearly" => "Gói Pro Năm",
            "monthly" => "Gói Premium Tháng",
            _ => "Gói Premium"
        };
    }

    private static string GetStatusLabel(string? status)
    {
        if (IsPaidStatus(status))
            return "Thành công";

        return NormalizePlanId(status ?? string.Empty) switch
        {
            "pending" => "Đang chờ",
            "cancelled" => "Đã hủy",
            "canceled" => "Đã hủy",
            "expired" => "Hết hạn",
            "failed" => "Thất bại",
            _ => "Đang xử lý"
        };
    }

    private static string GetPaymentMethodLabel(string? source)
    {
        return NormalizePlanId(source ?? string.Empty) switch
        {
            "google_play" => "Google Play",
            "payos" => "PayOS",
            _ => "Thanh toán"
        };
    }

    private static List<SubscriptionBenefitResponse> GetPlanBenefits(string? planId)
    {
        var normalizedPlanId = NormalizePlanId(planId ?? string.Empty);
        var isProPlan = normalizedPlanId.StartsWith("pro", StringComparison.OrdinalIgnoreCase);

        return new List<SubscriptionBenefitResponse>
        {
            new()
            {
                Icon = "scan",
                Title = "Lượt quét không giới hạn",
                Subtitle = "Phân tích vật thể AI nhanh hơn."
            },
            new()
            {
                Icon = "ai",
                Title = isProPlan ? "Học cùng AI chuyên sâu" : "AI Tutor cơ bản",
                Subtitle = isProPlan ? "Lộ trình cá nhân hóa 1:1." : "Gợi ý học tập theo tiến độ."
            },
            new()
            {
                Icon = "ads",
                Title = "Trải nghiệm không quảng cáo",
                Subtitle = "Tập trung hoàn toàn vào việc học."
            }
        };
    }

    private static string GetString(
        IReadOnlyDictionary<string, object> data,
        string fieldName,
        string fallback)
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

    private static DateTime? GetDateTime(IReadOnlyDictionary<string, object> data, string fieldName)
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

    private static long GenerateOrderCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt64(bytes);
        return (long)(100_000_000_000UL + value % 900_000_000_000UL);
    }

    private sealed record PayOSOrderRecord(
        string Uid,
        int Amount,
        DateTime PremiumExpiresAt);

    private sealed record PaymentOrderDocument(
        string Id,
        long? OrderCode,
        string PlanId,
        int Amount,
        string Status,
        string Source,
        string PaymentLinkId,
        string CheckoutUrl,
        DateTime? CreatedAt,
        DateTime? UpdatedAt,
        DateTime? PaidAt,
        DateTime? PremiumExpiresAt);
}
