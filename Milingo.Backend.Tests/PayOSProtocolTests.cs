using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Milingo.Backend.Models;
using Milingo.Backend.Services;
using Xunit;

namespace Milingo.Backend.Tests;

public class PayOSProtocolTests
{
    [Fact]
    public void ParseCreateOrder_reads_native_qr_fields()
    {
        const string json = """
            {
              "code": "00",
              "desc": "success",
              "data": {
                "bin": "970422",
                "accountNumber": "113366668888",
                "accountName": "MERCHANT NAME",
                "amount": 139000,
                "description": "Milingo pro",
                "orderCode": 123456789012,
                "currency": "VND",
                "paymentLinkId": "payment-link-id",
                "status": "PENDING",
                "checkoutUrl": "https://pay.payos.vn/web/payment-link-id",
                "qrCode": "00020101021238570010A000000727"
              }
            }
            """;
        var expiresAt = new DateTime(2026, 7, 15, 10, 30, 0, DateTimeKind.Utc);

        const string bankName = "Ngân hàng Thương mại Cổ phần Quân đội (MB)";
        var result = PayOSProtocol.ParseCreateOrder(json, expiresAt, bankName);

        Assert.Equal("970422", result.Bin);
        Assert.Equal(bankName, result.BankName);
        Assert.Equal("113366668888", result.AccountNumber);
        Assert.Equal("MERCHANT NAME", result.AccountName);
        Assert.Equal(139000, result.Amount);
        Assert.Equal("Milingo pro", result.Description);
        Assert.Equal(123456789012, result.OrderCode);
        Assert.Equal("payment-link-id", result.PaymentLinkId);
        Assert.Equal("PENDING", result.Status);
        Assert.Equal("https://pay.payos.vn/web/payment-link-id", result.CheckoutUrl);
        Assert.Equal("00020101021238570010A000000727", result.QrCode);
        Assert.Equal(expiresAt, result.ExpiresAt);
    }

    [Fact]
    public void VerifyWebhook_accepts_documented_body_signature_and_paid_codes()
    {
        var payload = CreateWebhookPayload();
        payload.Signature = Sign(payload.Data, "checksum-key");

        Assert.True(PayOSProtocol.VerifyWebhook(payload, "checksum-key"));
        Assert.True(PayOSProtocol.IsPaidWebhook(payload));
    }

    [Fact]
    public void VerifyWebhook_rejects_missing_or_modified_signature()
    {
        var payload = CreateWebhookPayload();

        Assert.False(PayOSProtocol.VerifyWebhook(payload, "checksum-key"));

        payload.Signature = Sign(payload.Data, "checksum-key");
        payload.Data.Amount++;

        Assert.False(PayOSProtocol.VerifyWebhook(payload, "checksum-key"));
    }

    [Fact]
    public void IsPaidWebhook_rejects_unsuccessful_payload()
    {
        var payload = CreateWebhookPayload();
        payload.Success = false;

        Assert.False(PayOSProtocol.IsPaidWebhook(payload));
    }

    private static PayOSWebhookPayload CreateWebhookPayload()
    {
        return new PayOSWebhookPayload
        {
            Code = "00",
            Desc = "success",
            Success = true,
            Data = new PayOSWebhookData
            {
                OrderCode = 123456789012,
                Amount = 139000,
                PaymentLinkId = "payment-link-id",
                Code = "00",
                Desc = "Thành công",
                ExtraData = new Dictionary<string, JsonElement>
                {
                    ["accountNumber"] = Json("12345678"),
                    ["currency"] = Json("VND"),
                    ["description"] = Json("Milingo pro"),
                    ["reference"] = Json("TF230204212323"),
                    ["transactionDateTime"] = Json("2026-07-15 10:00:00")
                }
            }
        };
    }

    private static string Sign(PayOSWebhookData data, string checksumKey)
    {
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = data.Amount,
            ["code"] = data.Code,
            ["desc"] = data.Desc,
            ["orderCode"] = data.OrderCode,
            ["paymentLinkId"] = data.PaymentLinkId
        };
        foreach (var item in data.ExtraData ?? new Dictionary<string, JsonElement>())
        {
            fields[item.Key] = item.Value.ValueKind == JsonValueKind.String
                ? item.Value.GetString()
                : item.Value.GetRawText();
        }

        var signedData = string.Join("&", fields.Select(item =>
            $"{item.Key}={Convert.ToString(item.Value, CultureInfo.InvariantCulture)}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(checksumKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedData)))
            .ToLowerInvariant();
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }
}
