using System.Globalization;
using BackEnd.Services;

namespace BackEnd.Models;

public sealed record NotificationSettings(bool Enabled, string LocalTime);

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
