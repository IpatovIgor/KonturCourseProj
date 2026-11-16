using System.Text;
using HabitTracker.Notifications.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace HabitTracker.Notifications.Tests;

public sealed class NotificationCursorTests
{
    private const string Id = "4d3b11f0-94e3-4e40-a936-8d1261dd189b";

    [Fact]
    public void CursorRoundTripsUtcTimeAndCanonicalId()
    {
        var value = new NotificationCursor(new DateTime(2026, 11, 15, 15, 0, 12, 456, DateTimeKind.Utc), Id);

        Assert.True(NotificationCursor.TryDecode(value.Encode(), out var decoded));
        Assert.Equal(value, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.CreatedAtUtc.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a===")]
    [InlineData("a")]
    [InlineData("a a")]
    public void InvalidBase64UrlIsRejected(string value) =>
        Assert.False(NotificationCursor.TryDecode(value, out _));

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"v":2,"createdAtUtc":"2026-11-15T15:00:12Z","id":"4d3b11f0-94e3-4e40-a936-8d1261dd189b"}""")]
    [InlineData("""{"v":1,"createdAtUtc":"2026-11-15T15:00:12+03:00","id":"4d3b11f0-94e3-4e40-a936-8d1261dd189b"}""")]
    [InlineData("""{"v":1,"createdAtUtc":"2026-11-15T15:00:12","id":"4d3b11f0-94e3-4e40-a936-8d1261dd189b"}""")]
    [InlineData("""{"v":1,"createdAtUtc":"2026-11-15T15:00:12Z","id":"invalid"}""")]
    public void InvalidCursorPayloadIsRejected(string json) =>
        Assert.False(NotificationCursor.TryDecode(WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(json)), out _));

    [Fact]
    public void OversizedCursorIsRejected() =>
        Assert.False(NotificationCursor.TryDecode(new string('a', 1025), out _));
}
