using HabitTracker.Contracts;

namespace BackEnd.Tests.Services;

public sealed class NotificationCalendarTests
{
    [Theory]
    [InlineData("2026-03-29", "02:45", "Europe/Berlin", "2026-03-29T01:00:00Z")]
    [InlineData("2026-10-25", "02:30", "Europe/Berlin", "2026-10-25T00:30:00Z")]
    [InlineData("2026-11-02", "00:30", "Asia/Yekaterinburg", "2026-11-01T19:30:00Z")]
    public void DueTimeUsesFirstValidInstant(string date, string time, string zone, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected),
            NotificationCalendar.DueAtUtc(DateOnly.Parse(date), TimeOnly.Parse(time), zone));
    }
}
