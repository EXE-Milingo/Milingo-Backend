using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

internal static class PayOSProtocol
{
    internal static CreatePayOSOrderResponse ParseCreateOrder(
        string responseBody,
        DateTime expiresAtUtc)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement)
            ? codeElement.GetString()
            : null;
        if (!string.Equals(code, "00", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PayOS rejected the payment order.");
        }

        var data = root.GetProperty("data");
        return new CreatePayOSOrderResponse
        {
            Bin = ReadString(data, "bin"),
            AccountNumber = ReadString(data, "accountNumber"),
            AccountName = ReadString(data, "accountName"),
            Amount = data.TryGetProperty("amount", out var amount)
                ? amount.GetInt32()
                : 0,
            Description = ReadString(data, "description"),
            CheckoutUrl = ReadString(data, "checkoutUrl"),
            OrderCode = data.TryGetProperty("orderCode", out var orderCode)
                ? orderCode.GetInt64()
                : 0,
            PaymentLinkId = ReadString(data, "paymentLinkId"),
            QrCode = ReadString(data, "qrCode"),
            Status = ReadString(data, "status", "PENDING"),
            ExpiresAt = DateTime.SpecifyKind(expiresAtUtc.ToUniversalTime(), DateTimeKind.Utc)
        };
    }

    internal static bool VerifyWebhook(PayOSWebhookPayload payload, string checksumKey)
    {
        if (string.IsNullOrWhiteSpace(payload.Signature)
            || string.IsNullOrWhiteSpace(checksumKey))
        {
            return false;
        }

        var fields = BuildWebhookSignatureFields(payload.Data);
        var expected = CreateSignature(fields, checksumKey);
        var actual = payload.Signature.Trim().ToLowerInvariant();
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(actual));
    }

    internal static bool IsPaidWebhook(PayOSWebhookPayload payload)
    {
        return payload.Success
            && string.Equals(payload.Code, "00", StringComparison.OrdinalIgnoreCase)
            && string.Equals(payload.Data.Code, "00", StringComparison.OrdinalIgnoreCase);
    }

    private static SortedDictionary<string, object?> BuildWebhookSignatureFields(
        PayOSWebhookData data)
    {
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = data.Amount,
            ["code"] = data.Code,
            ["desc"] = data.Desc,
            ["orderCode"] = data.OrderCode,
            ["paymentLinkId"] = data.PaymentLinkId
        };
        if (!string.IsNullOrWhiteSpace(data.Status))
        {
            fields["status"] = data.Status;
        }

        if (data.ExtraData is not null)
        {
            foreach (var item in data.ExtraData)
            {
                if (!fields.ContainsKey(item.Key)
                    && !string.Equals(item.Key, "signature", StringComparison.OrdinalIgnoreCase))
                {
                    fields[item.Key] = item.Value;
                }
            }
        }

        return fields;
    }

    private static string CreateSignature(
        IReadOnlyDictionary<string, object?> fields,
        string checksumKey)
    {
        var signedData = string.Join("&", fields.Select(item =>
            $"{item.Key}={ToSignatureString(item.Value)}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(checksumKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedData)))
            .ToLowerInvariant();
    }

    private static string ToSignatureString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            JsonElement json => JsonValue(json),
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string JsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => JsonSerializer.Serialize(value)
        };
    }

    private static string ReadString(
        JsonElement data,
        string propertyName,
        string fallback = "")
    {
        return data.TryGetProperty(propertyName, out var value)
            ? value.GetString() ?? fallback
            : fallback;
    }
}
