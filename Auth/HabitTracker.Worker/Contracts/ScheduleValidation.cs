using System.Globalization;
using MongoDB.Bson;

namespace HabitTracker.Worker.Contracts;

internal static class ScheduleValidation
{
    public static void Check(HabitScheduleSnapshot snapshot)
    {
        if (!ObjectId.TryParse(snapshot.UserId, out _) || !ObjectId.TryParse(snapshot.HabitId, out _) ||
            snapshot.Revision < 0 || snapshot.Status is not ("active" or "archived") ||
            !IsDate(snapshot.StartDate) || !IsTime(snapshot.ReminderLocalTime) ||
            !IsZone(snapshot.TimeZoneId) || snapshot.ScheduleVersions is not { Length: > 0 })
            throw new InvalidScheduleException("Invalid habit snapshot.");

        string? previous = null;
        foreach (var version in snapshot.ScheduleVersions)
        {
            if (version is null || !IsDate(version.EffectiveFrom) ||
                (previous is null ? version.EffectiveFrom != snapshot.StartDate :
                    string.CompareOrdinal(version.EffectiveFrom, previous) <= 0) ||
                version.DaysOfWeek is not { Length: > 0 } days || days.Any(day => day is < 1 or > 7) ||
                days.Distinct().Count() != days.Length)
                throw new InvalidScheduleException("Invalid schedule version.");
            previous = version.EffectiveFrom;
        }
    }

    public static void Check(SummaryScheduleSnapshot snapshot)
    {
        if (!ObjectId.TryParse(snapshot.UserId, out _) || snapshot.Revision < 0 ||
            !IsTime(snapshot.LocalTime) || !IsZone(snapshot.TimeZoneId))
            throw new InvalidScheduleException("Invalid summary snapshot.");
    }

    private static bool IsDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsZone(string value) =>
        !string.IsNullOrWhiteSpace(value) && TimeZoneInfo.TryFindSystemTimeZoneById(value, out _);
}

public sealed class InvalidScheduleException(string message) : Exception(message);
