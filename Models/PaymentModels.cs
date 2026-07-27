using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

// =================================================================
//  PAYOS DTOs
// =================================================================

public class CreatePayOSOrderRequest
{
    [Required(ErrorMessage = "PlanId is required.")]
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;
}

public class CreatePayOSOrderResponse
{
    [JsonPropertyName("bin")]
    public string Bin { get; set; } = string.Empty;

    [JsonPropertyName("bankName")]
    public string BankName { get; set; } = string.Empty;

    [JsonPropertyName("accountNumber")]
    public string AccountNumber { get; set; } = string.Empty;

    [JsonPropertyName("accountName")]
    public string AccountName { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("checkoutUrl")]
    public string CheckoutUrl { get; set; } = string.Empty;

    [JsonPropertyName("orderCode")]
    public long OrderCode { get; set; }

    [JsonPropertyName("paymentLinkId")]
    public string PaymentLinkId { get; set; } = string.Empty;

    [JsonPropertyName("qrCode")]
    public string QrCode { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "PENDING";

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }
}

public class SubscriptionPlanResponse
{
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    [JsonPropertyName("planName")]
    public string PlanName { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("durationDays")]
    public int DurationDays { get; set; }
}

public class PayOSWebhookPayload
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public PayOSWebhookData Data { get; set; } = new();

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

public class PayOSWebhookData
{
    [JsonPropertyName("orderCode")]
    public long OrderCode { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("paymentLinkId")]
    public string PaymentLinkId { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
}

// =================================================================
//  GOOGLE PLAY DTOs
// =================================================================

public class VerifyGooglePurchaseRequest
{
    [Required(ErrorMessage = "PurchaseToken is required.")]
    [JsonPropertyName("purchaseToken")]
    public string PurchaseToken { get; set; } = string.Empty;

    [Required(ErrorMessage = "ProductId is required.")]
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = string.Empty;

    [Required(ErrorMessage = "PackageName is required.")]
    [JsonPropertyName("packageName")]
    public string PackageName { get; set; } = string.Empty;
}

public class VerifyGooglePurchaseResponse
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }
}

public class PremiumStatusResponse
{
    [JsonPropertyName("isPremium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

public class PayOSOrderStatusResponse
{
    [JsonPropertyName("orderCode")]
    public long OrderCode { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "PENDING";

    [JsonPropertyName("isPaid")]
    public bool IsPaid { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }
}

public class SubscriptionBenefitResponse
{
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string Subtitle { get; set; } = string.Empty;
}

public class SubscriptionOverviewResponse
{
    [JsonPropertyName("isPremium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("planId")]
    public string? PlanId { get; set; }

    [JsonPropertyName("planName")]
    public string PlanName { get; set; } = "Gói miễn phí";

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("remainingDays")]
    public int? RemainingDays { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("memberSince")]
    public DateTime? MemberSince { get; set; }

    [JsonPropertyName("lastPaymentAt")]
    public DateTime? LastPaymentAt { get; set; }

    [JsonPropertyName("lastPaymentAmount")]
    public int LastPaymentAmount { get; set; }

    [JsonPropertyName("nextPaymentAmount")]
    public int NextPaymentAmount { get; set; }

    [JsonPropertyName("monthlyProgressPercent")]
    public int MonthlyProgressPercent { get; set; }

    [JsonPropertyName("benefits")]
    public List<SubscriptionBenefitResponse> Benefits { get; set; } = new();
}

public class PaymentTransactionHistoryResponse
{
    [JsonPropertyName("totalSpentThisYear")]
    public int TotalSpentThisYear { get; set; }

    [JsonPropertyName("transactions")]
    public List<PaymentTransactionResponse> Transactions { get; set; } = new();
}

public class PaymentTransactionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("orderCode")]
    public long? OrderCode { get; set; }

    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    [JsonPropertyName("planName")]
    public string PlanName { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("statusLabel")]
    public string StatusLabel { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("paymentMethodLabel")]
    public string PaymentMethodLabel { get; set; } = string.Empty;

    [JsonPropertyName("paymentLinkId")]
    public string? PaymentLinkId { get; set; }

    [JsonPropertyName("checkoutUrl")]
    public string? CheckoutUrl { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("paidAt")]
    public DateTime? PaidAt { get; set; }
}
