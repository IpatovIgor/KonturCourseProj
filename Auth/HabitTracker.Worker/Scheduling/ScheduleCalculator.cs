using System.Globalization;
using HabitTracker.Contracts;
using HabitTracker.Worker.Models;

namespace HabitTracker.Worker.Scheduling;

public sealed record DueSlot(string LocalDate, DateTime DueAtUtc);

public sealed class ScheduleCalculator
{
    // Ближайший срок для привычки. afterLocalDate — исключительная нижняя граница.
    public DueSlot? NextHabitDue(HabitSchedule snapshot, DateTimeOffset now, string? afterLocalDate)
    {
        if (snapshot.Status != "active" || !snapshot.ReminderEnabled)
            return null;

        var time = TimeOnly.ParseExact(snapshot.ReminderLocalTime, "HH:mm", CultureInfo.InvariantCulture);
        var today = NotificationCalendar.LocalDate(now, snapshot.TimeZoneId);

        var start = today;
        if (afterLocalDate is not null)
        {
            var after = DateOnly.ParseExact(afterLocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (after >= start)
                start = after.AddDays(1);
        }

        if (DateOnly.TryParseExact(snapshot.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var startDate) && start < startDate)
            start = startDate;

        if (snapshot.ReminderFirstEligibleDate is { } firstEligibleDate)
        {
            var firstDate = DateOnly.ParseExact(firstEligibleDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (start < firstDate)
                start = firstDate;
        }

        var versions = snapshot.ScheduleVersions.OrderBy(version => version.EffectiveFrom, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < versions.Length; i++)
        {
            var version = versions[i];
            var effective = DateOnly.ParseExact(version.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var first = start > effective ? start : effective;
            var last = i + 1 < versions.Length
                ? DateOnly.ParseExact(versions[i + 1].EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber - 1
                : DateOnly.MaxValue.DayNumber;
            for (var n = first.DayNumber; n <= Math.Min(last, first.DayNumber + 6); n++)
            {
                var date = DateOnly.FromDayNumber(n);
                var day = ((int)date.DayOfWeek + 6) % 7 + 1;
                if (version.DaysOfWeek.Contains(day))
                    return new DueSlot(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        NotificationCalendar.DueAtUtc(date, time, snapshot.TimeZoneId).UtcDateTime);
            }
        }

        return null;
    }

    // Ближайший срок для сводки. Включённая сводка подходит на каждый день.
    public DueSlot? NextSummaryDue(SummarySchedule snapshot, DateTimeOffset now, string? afterLocalDate)
    {
        if (!snapshot.Enabled)
            return null;

        var time = TimeOnly.ParseExact(snapshot.LocalTime, "HH:mm", CultureInfo.InvariantCulture);
        var today = NotificationCalendar.LocalDate(now, snapshot.TimeZoneId);

        var start = today;
        if (afterLocalDate is not null)
        {
            var after = DateOnly.ParseExact(afterLocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (after >= start)
                start = after.AddDays(1);
        }

        return new DueSlot(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            NotificationCalendar.DueAtUtc(start, time, snapshot.TimeZoneId).UtcDateTime);
    }
}
