using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Extensions;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

//payment
[ApiController]
[Route("api/v1/payments")]
[Authorize]
public class PaymentController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly IFirestoreService _firestoreService;
    private readonly ILogger<PaymentController> _logger;

    public PaymentController(
        IPaymentService paymentService,
        IFirestoreService firestoreService,
        ILogger<PaymentController> logger)
    {
        _paymentService = paymentService;
        _firestoreService = firestoreService;
        _logger = logger;
    }

    [HttpGet("plans")]
    public IActionResult GetSubscriptionPlans()
    {
        try
        {
            return Ok(new ApiResponse<IReadOnlyList<SubscriptionPlanResponse>>
            {
                Status = "success",
                Message = "Subscription plans retrieved successfully.",
                Data = _paymentService.GetSubscriptionPlans()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting subscription plans.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse("An unexpected error occurred. Please try again."));
        }
    }

    [HttpPost("payos/create-order")]
    public async Task<IActionResult> CreatePayOSOrder(
        [FromBody] CreatePayOSOrderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

            if (!ModelState.IsValid)
                return BadRequest(ErrorResponse(ModelState));

            var result = await _paymentService.CreatePayOSOrderAsync(
                uid,
                request.PlanId,
                request.ReturnUrl,
                request.CancelUrl,
                cancellationToken);

            return Ok(new ApiResponse<CreatePayOSOrderResponse>
            {
                Status = "success",
                Message = "PayOS checkout order created successfully.",
                Data = result
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Create PayOS order request cancelled: client disconnected.");
            return StatusCode(499);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating PayOS order.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse("An unexpected error occurred. Please try again."));
        }
    }

    [HttpPost("payos/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> PayOSWebhook(
        [FromBody] PayOSWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var signature = Request.Headers.TryGetValue("x-payos-signature", out var values)
                ? values.FirstOrDefault() ?? string.Empty
                : string.Empty;

            var handled = await _paymentService.HandlePayOSWebhookAsync(
                payload,
                signature,
                cancellationToken);

            return Ok(new ApiResponse<object>
            {
                Status = "success",
                Message = handled ? "Webhook processed." : "Webhook received."
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("PayOS webhook request cancelled.");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling PayOS webhook.");
            return Ok(new ApiResponse<object>
            {
                Status = "success",
                Message = "Webhook received."
            });
        }
    }

    [HttpGet("payos/redirect")]
    [AllowAnonymous]
    public IActionResult PayOSRedirect(
        [FromQuery] string status,
        [FromQuery] long orderCode)
    {
        var isSuccess = !string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        var appPath = isSuccess ? "success" : "cancel";
        var appUrl = $"milingo://payment/payment/{appPath}?orderCode={orderCode}&status={status}";

        var html = $$"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Redirecting to MiLingo...</title>
            <script>
                window.onload = function() {
                    window.location.href = "{{appUrl}}";
                    setTimeout(function() {
                        var fallback = document.getElementById('fallback');
                        if (fallback) fallback.style.display = 'block';
                    }, 2500);
                };
            </script>
            <style>
                body {
                    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
                    text-align: center;
                    padding: 50px 20px;
                    background-color: #F7FAF5;
                    color: #17201B;
                }
                h2 { margin-bottom: 10px; color: #0B7A75; }
                p { color: #66736C; font-size: 16px; }
                .btn {
                    display: inline-block;
                    padding: 12px 24px;
                    background-color: #0B7A75;
                    color: white;
                    text-decoration: none;
                    border-radius: 8px;
                    font-weight: bold;
                    margin-top: 20px;
                    box-shadow: 0 4px 6px rgba(11, 122, 117, 0.2);
                }
            </style>
        </head>
        <body>
            <h2>Đang chuyển hướng về ứng dụng MiLingo...</h2>
            <p>Vui lòng đợi trong giây lát.</p>
            <div id="fallback" style="display:none;">
                <p>Nếu ứng dụng không tự động mở, vui lòng nhấn nút bên dưới:</p>
                <a class="btn" href="{{appUrl}}">Mở ứng dụng MiLingo</a>
            </div>
        </body>
        </html>
        """;

        return Content(html, "text/html", Encoding.UTF8);
    }

    [HttpPost("payos/verify-order/{orderCode}")]
    public async Task<IActionResult> VerifyOrder(
        long orderCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

            var verified = await _paymentService.VerifyPayOSOrderAsync(orderCode, cancellationToken);

            return Ok(new ApiResponse<object>
            {
                Status = verified ? "success" : "error",
                Message = verified ? "Order verified and paid." : "Order is not paid or verification failed."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error verifying PayOS order {OrderCode}.", orderCode);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse("An unexpected error occurred. Please try again."));
        }
    }

    [HttpPost("google/verify-purchase")]
    public async Task<IActionResult> VerifyGooglePurchase(
        [FromBody] VerifyGooglePurchaseRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

            if (!ModelState.IsValid)
                return BadRequest(ErrorResponse(ModelState));

            var result = await _paymentService.VerifyGooglePurchaseAsync(
                uid,
                request.PurchaseToken,
                request.ProductId,
                request.PackageName,
                cancellationToken);

            return Ok(new ApiResponse<VerifyGooglePurchaseResponse>
            {
                Status = result.IsValid ? "success" : "error",
                Message = result.IsValid
                    ? "Google Play purchase verified successfully."
                    : "Google Play purchase is invalid or expired.",
                Data = result
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Verify Google purchase request cancelled: client disconnected.");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error verifying Google purchase.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse("An unexpected error occurred. Please try again."));
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetPremiumStatus(CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

            // Auto-sync any pending PayOS orders
            await _paymentService.SyncPendingPayOSOrdersAsync(uid, cancellationToken);

            var result = await _firestoreService.GetPremiumStatusAsync(uid, cancellationToken);

            return Ok(new ApiResponse<PremiumStatusResponse>
            {
                Status = "success",
                Message = "Premium status retrieved successfully.",
                Data = result
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Get premium status request cancelled: client disconnected.");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting premium status.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse("An unexpected error occurred. Please try again."));
        }
    }

    [HttpGet("subscription")]
    public async Task<IActionResult> GetSubscriptionOverview(CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

            var result = await _paymentService.GetSubscriptionOverviewAsync(uid, cancellationToken);

            return Ok(new ApiResponse<SubscriptionOverviewResponse>
            {
                Status = "success",
                Message = "Subscription overview retrieved successfully.",
                Data = result
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Get subscription overview request cancelled: client disconnected.");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting subscription overview.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse("An unexpected error occurred. Please try again."));
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetPaymentTransactionHistory(CancellationToken cancellationToken)
    {
        try
        {
            var uid = User.GetFirebaseUid();
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(ErrorResponse("Invalid token: User identifier not found in claims."));

            var result = await _paymentService.GetPaymentTransactionHistoryAsync(uid, cancellationToken);

            return Ok(new ApiResponse<PaymentTransactionHistoryResponse>
            {
                Status = "success",
                Message = "Payment transaction history retrieved successfully.",
                Data = result
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Get payment history request cancelled: client disconnected.");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting payment history.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse("An unexpected error occurred. Please try again."));
        }
    }

    private static ApiResponse<object> ErrorResponse(string message)
    {
        return new ApiResponse<object> { Status = "error", Message = message };
    }

    private static ApiResponse<object> ErrorResponse(Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState)
    {
        var errors = modelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .ToList();

        return new ApiResponse<object>
        {
            Status = "error",
            Message = string.Join(" ", errors)
        };
    }
}
