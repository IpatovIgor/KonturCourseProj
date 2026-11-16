using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace HabitTracker.Notifications.Services;

public readonly record struct NotificationCursor(DateTime CreatedAtUtc, string Id)
{
    public string Encode() => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
        new { v = 1, createdAtUtc = CreatedAtUtc, id = Id }));

    public static bool TryDecode(string value, out NotificationCursor cursor)
    {
        cursor = default;
        if (value.Length is 0 or > 1024 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            return false;

        try
        {
            var bytes = WebEncoders.Base64UrlDecode(value);
            if (value != WebEncoders.Base64UrlEncode(bytes))
                return false;

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("createdAtUtc", out var date) || date.ValueKind != JsonValueKind.String ||
                !date.TryGetDateTime(out var time) || time.Kind != DateTimeKind.Utc ||
                !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(id.GetString(), "D", out var uuid) || uuid == Guid.Empty)
                return false;

            cursor = new NotificationCursor(time, uuid.ToString("D"));
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return false;
        }
    }
}
