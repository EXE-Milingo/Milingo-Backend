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
    private static readonly PaymentPlanDefinition[] PaymentPlans =
    {
        new("plus", "Plus", "Gói Plus"),
        new("pro", "Pro", "Gói Pro"),
        new("ultra", "Ultra", "Gói Ultra")
    };

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
    public IReadOnlyList<SubscriptionPlanResponse> GetSubscriptionPlans()
    {
        return PaymentPlans
            .Select(plan => new SubscriptionPlanResponse
            {
                PlanId = plan.Id,
                PlanName = plan.DisplayName,
                Amount = GetConfiguredPlanAmount(plan),
                DurationDays = GetConfiguredPlanDurationDays(plan)
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CreatePayOSOrderResponse> CreatePayOSOrderAsync(
        string uid,
        string planId,
        CancellationToken cancellationToken = default)
    {
        var clientId = GetRequiredConfig("PayOS:ClientId");
        var apiKey = GetRequiredConfig("PayOS:ApiKey");
        var checksumKey = GetRequiredConfig("PayOS:ChecksumKey");
        var bankName = GetRequiredConfig("PayOS:BankName");
        var returnUrl = GetRequiredConfig("PayOS:ReturnUrl");
        var cancelUrl = GetRequiredConfig("PayOS:CancelUrl");
        var amount = GetPlanAmount(planId);
        var normalizedPlanId = NormalizePlanId(planId);
        var durationDays = GetConfiguredPlanDurationDays(
            GetPaymentPlan(normalizedPlanId));
        var reusableOrder = await ResolveExistingOrderAsync(
            uid,
            normalizedPlanId,
            cancellationToken);
        if (reusableOrder is not null)
        {
            return reusableOrder;
        }

        var orderCode = GenerateOrderCode();
        var orderExpiryMinutes = Math.Clamp(
            _configuration.GetValue<int?>("PayOS:OrderExpiryMinutes") ?? 30,
            5,
            60);
        var orderExpiresAt = DateTime.UtcNow.AddMinutes(orderExpiryMinutes);
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
            { "signature", CreateHmacSignature(signatureFields, checksumKey) },
            { "expiredAt", new DateTimeOffset(orderExpiresAt).ToUnixTimeSeconds() }
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

        var result = PayOSProtocol.ParseCreateOrder(
            responseBody,
            orderExpiresAt,
            bankName);
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
            result,
            durationDays,
            cancellationToken);

        _logger.LogInformation(
            "Created PayOS order {OrderCode} for user '{Uid}'. PaymentLinkId='{PaymentLinkId}'.",
            result.OrderCode, uid, result.PaymentLinkId);

        return result;
    }

    public async Task<CreatePayOSOrderResponse?> GetPendingPayOSOrderAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        var orders = await GetUserPaymentOrdersAsync(uid, cancellationToken);
        foreach (var order in orders
                     .Where(IsPendingPayOSOrder)
                     .OrderByDescending(GetOrderSortDate))
        {
            if (!order.OrderCode.HasValue)
            {
                continue;
            }

            var status = await VerifyPayOSOrderAsync(
                uid,
                order.OrderCode.Value,
                cancellationToken);
            if (!string.Equals(status.Status, "PENDING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasRecoverableQr(order))
            {
                return ToCreateOrderResponse(order, status.Status);
            }

            await CancelPayOSOrderAsync(uid, order.OrderCode.Value, cancellationToken);
        }

        return null;
    }

    public async Task<bool> CancelPayOSOrderAsync(
        string uid,
        long orderCode,
        CancellationToken cancellationToken = default)
    {
        var orderRef = _db.Collection("payment_orders")
            .Document(orderCode.ToString(CultureInfo.InvariantCulture));
        var snapshot = await orderRef.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Exists)
        {
            throw new KeyNotFoundException($"Payment order {orderCode} was not found.");
        }

        var ownerUid = snapshot.GetValue<string>("uid");
        if (!string.Equals(uid, ownerUid, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The payment order belongs to another user.");
        }

        var status = snapshot.ContainsField("status")
            ? snapshot.GetValue<string>("status")
            : "PENDING";
        if (!string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var clientId = GetRequiredConfig("PayOS:ClientId");
        var apiKey = GetRequiredConfig("PayOS:ApiKey");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{PayOSPaymentRequestsUrl}/{orderCode}/cancel")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new { cancellationReason = "User selected another MiLingo plan" },
                    _jsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("x-client-id", clientId);
        request.Headers.Add("x-api-key", apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "PayOS cancellation failed for order {OrderCode} with HTTP {StatusCode}.",
                orderCode,
                (int)response.StatusCode);
            return false;
        }

        await MarkPayOSOrderStatusAsync(orderCode, "CANCELLED", cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HandlePayOSWebhookAsync(
        PayOSWebhookPayload payload,
        string signature,
        CancellationToken cancellationToken = default)
    {
        var checksumKey = GetRequiredConfig("PayOS:ChecksumKey");
        if (string.IsNullOrWhiteSpace(payload.Signature))
        {
            payload.Signature = signature;
        }

        if (!PayOSProtocol.VerifyWebhook(payload, checksumKey))
        {
            _logger.LogWarning(
                "PayOS webhook rejected: invalid signature for order {OrderCode}.",
                payload.Data.OrderCode);
            return false;
        }

        _logger.LogInformation(
            "PayOS webhook verified for order {OrderCode}. Code='{Code}', TransactionCode='{TransactionCode}'.",
            payload.Data.OrderCode, payload.Code, payload.Data.Code);

        if (!PayOSProtocol.IsPaidWebhook(payload))
        {
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

        if (IsPaidStatus(order.Status))
        {
            return true;
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
    public async Task<PayOSOrderStatusResponse> VerifyPayOSOrderAsync(
        string uid,
        long orderCode,
        CancellationToken cancellationToken = default)
    {
        var clientId = GetRequiredConfig("PayOS:ClientId");
        var apiKey = GetRequiredConfig("PayOS:ApiKey");
        var orderRef = _db.Collection("payment_orders")
            .Document(orderCode.ToString(CultureInfo.InvariantCulture));
        var snapshot = await orderRef.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Exists)
        {
            throw new KeyNotFoundException($"Payment order {orderCode} was not found.");
        }

        var ownerUid = snapshot.GetValue<string>("uid");
        if (!string.Equals(ownerUid, uid, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The payment order belongs to another user.");
        }

        var currentStatus = snapshot.ContainsField("status")
            ? snapshot.GetValue<string>("status")
            : "PENDING";
        var orderExpiresAt = snapshot.ContainsField("expiresAt")
            ? snapshot.GetValue<Timestamp>("expiresAt").ToDateTime()
            : (DateTime?)null;
        if (IsPaidStatus(currentStatus))
        {
            return BuildOrderStatus(orderCode, currentStatus, orderExpiresAt);
        }

        var url = $"{PayOSPaymentRequestsUrl}/{orderCode}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-client-id", clientId);
        request.Headers.Add("x-api-key", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "PayOS query order {OrderCode} failed with HTTP {StatusCode}.",
                    orderCode,
                    (int)response.StatusCode);
                return BuildOrderStatus(orderCode, currentStatus, orderExpiresAt);
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<PayOSWebhookPayload>(responseBody, _jsonOptions);
            if (payload is null
                || !string.Equals(payload.Code, "00", StringComparison.OrdinalIgnoreCase))
            {
                return BuildOrderStatus(orderCode, currentStatus, orderExpiresAt);
            }

            var payosStatus = string.IsNullOrWhiteSpace(payload.Data.Status)
                ? currentStatus
                : payload.Data.Status.ToUpperInvariant();
            if (IsPaidStatus(payosStatus))
            {
                var premiumExpiresAt = snapshot.ContainsField("premiumExpiresAt")
                    ? snapshot.GetValue<Timestamp>("premiumExpiresAt").ToDateTime()
                    : GetPremiumExpiresAt(snapshot.ContainsField("planId")
                        ? snapshot.GetValue<string>("planId")
                        : "monthly");
                await _firestoreService.SetPremiumAsync(
                    uid,
                    premiumExpiresAt,
                    "payos",
                    cancellationToken);
                await MarkPayOSOrderStatusAsync(orderCode, "PAID", cancellationToken);
                return BuildOrderStatus(orderCode, "PAID", orderExpiresAt);
            }

            if (string.Equals(payosStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(payosStatus, "EXPIRED", StringComparison.OrdinalIgnoreCase))
            {
                await MarkPayOSOrderStatusAsync(orderCode, payosStatus, cancellationToken);
                return BuildOrderStatus(orderCode, payosStatus, orderExpiresAt);
            }

            if (orderExpiresAt.HasValue && orderExpiresAt.Value <= DateTime.UtcNow)
            {
                await MarkPayOSOrderStatusAsync(orderCode, "EXPIRED", cancellationToken);
                return BuildOrderStatus(orderCode, "EXPIRED", orderExpiresAt);
            }

            return BuildOrderStatus(orderCode, payosStatus, orderExpiresAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying PayOS order {OrderCode}.", orderCode);
            return BuildOrderStatus(orderCode, currentStatus, orderExpiresAt);
        }
    }

    /// <inheritdoc />
    public async Task SyncPendingPayOSOrdersAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var orders = await GetUserPaymentOrdersAsync(uid, cancellationToken);
            var pendingPayOSOrders = orders
                .Where(o => string.Equals(o.Source, "payos", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(o.Status, "PENDING", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pendingPayOSOrders.Count == 0)
                return;

            _logger.LogInformation("Found {Count} pending PayOS orders for user '{Uid}'. Syncing status...", pendingPayOSOrders.Count, uid);

            foreach (var order in pendingPayOSOrders)
            {
                if (order.OrderCode.HasValue)
                {
                    await VerifyPayOSOrderAsync(uid, order.OrderCode.Value, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing pending PayOS orders for user '{Uid}'.", uid);
        }
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

    private async Task<CreatePayOSOrderResponse?> ResolveExistingOrderAsync(
        string uid,
        string requestedPlanId,
        CancellationToken cancellationToken)
    {
        var orders = await GetUserPaymentOrdersAsync(uid, cancellationToken);
        foreach (var order in orders
                     .Where(IsPendingPayOSOrder)
                     .OrderByDescending(GetOrderSortDate))
        {
            if (!order.OrderCode.HasValue)
            {
                continue;
            }

            var verified = await VerifyPayOSOrderAsync(
                uid,
                order.OrderCode.Value,
                cancellationToken);
            var action = PaymentOrderPolicy.Decide(
                requestedPlanId,
                order.PlanId,
                verified.Status,
                order.ExpiresAt ?? DateTime.MinValue,
                DateTime.UtcNow);
            if (action == PaymentOrderAction.RefreshEntitlement)
            {
                return ToCreateOrderResponse(order, "PAID");
            }

            if (action == PaymentOrderAction.Reuse && HasRecoverableQr(order))
            {
                return ToCreateOrderResponse(order, verified.Status);
            }

            var mustCancel = action is PaymentOrderAction.Reuse
                or PaymentOrderAction.CancelAndReplace
                || (action == PaymentOrderAction.Replace
                    && string.Equals(
                        verified.Status,
                        "PENDING",
                        StringComparison.OrdinalIgnoreCase));
            if (mustCancel)
            {
                var cancelled = await CancelPayOSOrderAsync(
                    uid,
                    order.OrderCode.Value,
                    cancellationToken);
                if (!cancelled)
                {
                    throw new InvalidOperationException(
                        "The existing PayOS order could not be cancelled safely.");
                }
            }
        }

        return null;
    }

    private static bool IsPendingPayOSOrder(PaymentOrderDocument order)
    {
        return string.Equals(order.Source, "payos", StringComparison.OrdinalIgnoreCase)
            && string.Equals(order.Status, "PENDING", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRecoverableQr(PaymentOrderDocument order)
    {
        return !string.IsNullOrWhiteSpace(order.QrCode)
            && !string.IsNullOrWhiteSpace(order.AccountNumber)
            && !string.IsNullOrWhiteSpace(order.Description)
            && order.Amount > 0
            && order.ExpiresAt.HasValue;
    }

    private CreatePayOSOrderResponse ToCreateOrderResponse(
        PaymentOrderDocument order,
        string status)
    {
        return new CreatePayOSOrderResponse
        {
            Bin = order.Bin,
            BankName = PaymentOrderPolicy.ResolveBankName(
                order.BankName,
                GetRequiredConfig("PayOS:BankName")),
            AccountNumber = order.AccountNumber,
            AccountName = order.AccountName,
            Amount = order.Amount,
            Description = order.Description,
            CheckoutUrl = order.CheckoutUrl,
            OrderCode = order.OrderCode ?? 0,
            PaymentLinkId = order.PaymentLinkId,
            QrCode = order.QrCode,
            Status = status,
            ExpiresAt = order.ExpiresAt ?? DateTime.MinValue
        };
    }

    private static PayOSOrderStatusResponse BuildOrderStatus(
        long orderCode,
        string status,
        DateTime? expiresAt)
    {
        return new PayOSOrderStatusResponse
        {
            OrderCode = orderCode,
            Status = status,
            IsPaid = IsPaidStatus(status),
            ExpiresAt = expiresAt
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
        CreatePayOSOrderResponse payment,
        int durationDays,
        CancellationToken cancellationToken)
    {
        var orderRef = _db.Collection("payment_orders")
            .Document(orderCode.ToString(CultureInfo.InvariantCulture));
        await orderRef.SetAsync(new Dictionary<string, object>
        {
            { "uid", uid },
            { "planId", planId },
            { "amount", amount },
            { "orderCode", orderCode },
            { "paymentLinkId", paymentLinkId },
            { "checkoutUrl", checkoutUrl },
            { "bin", payment.Bin },
            { "bankName", payment.BankName },
            { "accountNumber", payment.AccountNumber },
            { "accountName", payment.AccountName },
            { "description", payment.Description },
            { "qrCode", payment.QrCode },
            { "expiresAt", Timestamp.FromDateTime(DateTime.SpecifyKind(
                payment.ExpiresAt.ToUniversalTime(),
                DateTimeKind.Utc)) },
            { "status", "PENDING" },
            { "durationDays", durationDays },
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
            snapshot.ContainsField("status") ? snapshot.GetValue<string>("status") : "PENDING",
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
        return GetConfiguredPlanAmount(GetPaymentPlan(planId));
    }

    private int? TryGetConfiguredPlanAmount(string? planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return null;

        try
        {
            return GetPlanAmount(planId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private DateTime GetPremiumExpiresAt(string planId)
    {
        var durationDays = GetConfiguredPlanDurationDays(GetPaymentPlan(planId));
        return DateTime.UtcNow.AddDays(durationDays);
    }

    private PaymentPlanDefinition GetPaymentPlan(string planId)
    {
        var normalizedPlanId = NormalizePlanId(planId);
        var plan = PaymentPlans.FirstOrDefault(candidate => candidate.Id == normalizedPlanId);

        return plan ?? throw new ArgumentException(
            $"Unsupported subscription plan '{planId}'. Supported plans: plus, pro, ultra.",
            nameof(planId));
    }

    private int GetConfiguredPlanAmount(PaymentPlanDefinition plan)
    {
        var amount = _configuration.GetValue<int?>($"Subscriptions:{plan.SectionName}:Amount");
        if (amount is null or <= 0)
        {
            throw new InvalidOperationException($"Subscription plan '{plan.Id}' is not configured.");
        }

        return amount.Value;
    }

    private int GetConfiguredPlanDurationDays(PaymentPlanDefinition plan)
    {
        var durationDays = _configuration.GetValue<int?>(
            $"Subscriptions:{plan.SectionName}:DurationDays");
        if (durationDays is null or <= 0)
        {
            throw new InvalidOperationException($"Subscription plan '{plan.Id}' has no valid duration.");
        }

        return durationDays.Value;
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
            GetString(data, "bin", string.Empty),
            GetString(data, "bankName", string.Empty),
            GetString(data, "accountNumber", string.Empty),
            GetString(data, "accountName", string.Empty),
            GetString(data, "description", string.Empty),
            GetString(data, "qrCode", string.Empty),
            GetDateTime(data, "expiresAt"),
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
            "plus" => "Gói Plus",
            "pro" => "Gói Pro",
            "ultra" => "Gói Ultra",
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
        string Status,
        DateTime PremiumExpiresAt);

    private sealed record PaymentPlanDefinition(
        string Id,
        string SectionName,
        string DisplayName);

    private sealed record PaymentOrderDocument(
        string Id,
        long? OrderCode,
        string PlanId,
        int Amount,
        string Status,
        string Source,
        string PaymentLinkId,
        string CheckoutUrl,
        string Bin,
        string BankName,
        string AccountNumber,
        string AccountName,
        string Description,
        string QrCode,
        DateTime? ExpiresAt,
        DateTime? CreatedAt,
        DateTime? UpdatedAt,
        DateTime? PaidAt,
        DateTime? PremiumExpiresAt);
}
