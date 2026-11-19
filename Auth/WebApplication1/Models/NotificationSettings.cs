using System.Globalization;
using BackEnd.Services;
using HabitTracker.Contracts;

namespace BackEnd.Models;

public sealed record NotificationSettings(bool Enabled, string LocalTime)
{
    public string? FirstEligibleDate(DateTimeOffset now, string zone)
    {
        if (!Enabled)
            return null;

        var date = NotificationCalendar.LocalDate(now, zone);
        var time = TimeOnly.ParseExact(LocalTime, "HH:mm", CultureInfo.InvariantCulture);
        if (NotificationCalendar.DueAtUtc(date, time, zone) < now)
            date = date.AddDays(1);
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

public sealed record NotificationSettingsRequest(bool? Enabled, string? LocalTime)
{
    public NotificationSettings Validate()
    {
        if (Enabled is null || LocalTime is null || !TimeOnly.TryParseExact(LocalTime, "HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw HabitApiException.Validation("Укажите enabled и время в формате HH:mm");

        return new(Enabled.Value, LocalTime);
    }
}
