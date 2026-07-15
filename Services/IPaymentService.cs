using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

public interface IPaymentService
{
    IReadOnlyList<SubscriptionPlanResponse> GetSubscriptionPlans();

    Task<CreatePayOSOrderResponse> CreatePayOSOrderAsync(
        string uid,
        string planId,
        CancellationToken cancellationToken = default);

    Task<CreatePayOSOrderResponse?> GetPendingPayOSOrderAsync(
        string uid,
        CancellationToken cancellationToken = default);

    Task<bool> HandlePayOSWebhookAsync(
        PayOSWebhookPayload payload,
        string signature,
        CancellationToken cancellationToken = default);

    Task<PayOSOrderStatusResponse> VerifyPayOSOrderAsync(
        string uid,
        long orderCode,
        CancellationToken cancellationToken = default);

    Task<bool> CancelPayOSOrderAsync(
        string uid,
        long orderCode,
        CancellationToken cancellationToken = default);

    Task SyncPendingPayOSOrdersAsync(
        string uid,
        CancellationToken cancellationToken = default);

    Task<VerifyGooglePurchaseResponse> VerifyGooglePurchaseAsync(
        string uid,
        string purchaseToken,
        string productId,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<SubscriptionOverviewResponse> GetSubscriptionOverviewAsync(
        string uid,
        CancellationToken cancellationToken = default);

    Task<PaymentTransactionHistoryResponse> GetPaymentTransactionHistoryAsync(
        string uid,
        CancellationToken cancellationToken = default);
}
