using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

public static class SnapHistoryCursorCodec
{
    public static int NormalizeLimit(int requested) => Math.Clamp(requested, 1, 20);

    public static string Encode(SnapHistoryCursor cursor)
    {
        var payload = new CursorPayload(cursor.CreatedAt.ToUniversalTime(), cursor.DocumentId);
        var json = JsonSerializer.Serialize(payload);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? value, out SnapHistoryCursor cursor)
    {
        cursor = default!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = (base64.Length % 4) switch
            {
                0 => base64,
                2 => base64 + "==",
                3 => base64 + "=",
                _ => throw new FormatException("Invalid Base64 URL length."),
            };

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var payload = JsonSerializer.Deserialize<CursorPayload>(json);
            if (payload is null ||
                payload.CreatedAt == default ||
                string.IsNullOrWhiteSpace(payload.DocumentId))
            {
                return false;
            }

            cursor = new SnapHistoryCursor(
                payload.CreatedAt.ToUniversalTime(),
                payload.DocumentId);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record CursorPayload(
        [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
        [property: JsonPropertyName("documentId")] string DocumentId);
}
