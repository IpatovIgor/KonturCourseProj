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

        for (var date = start; date <= today.AddYears(1); date = date.AddDays(1))
        {
            if (!IsScheduled(snapshot, date))
                continue;

            var due = NotificationCalendar.DueAtUtc(date, time, snapshot.TimeZoneId);

            // Сегодняшний срок в прошлом — всё равно возвращаем (догоняющее уведомление за сегодня).
            if (date == today && due < now)
                return new DueSlot(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), due.UtcDateTime);
            if (due >= now)
                return new DueSlot(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), due.UtcDateTime);
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

        var date = start;
        var due = NotificationCalendar.DueAtUtc(date, time, snapshot.TimeZoneId);

        // Если сегодняшний срок уже прошёл — это догоняющее уведомление за сегодня.
        if (date == today && due < now)
            return new DueSlot(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), due.UtcDateTime);

        if (due >= now)
            return new DueSlot(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), due.UtcDateTime);

        // Будущий день.
        date = today.AddDays(1);
        due = NotificationCalendar.DueAtUtc(date, time, snapshot.TimeZoneId);
        return new DueSlot(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), due.UtcDateTime);
    }

    // Точно как BackEnd.Services.HabitService.IsScheduled / ScheduleOn:
    // DaysOfWeek в контракте: Monday=1 ... Sunday=7.
    private static bool IsScheduled(HabitSchedule snapshot, DateOnly date)
    {
        var text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.CompareOrdinal(text, snapshot.StartDate) < 0)
            return false;
        var version = ScheduleOn(snapshot, text);
        if (version is null)
            return false;
        var day = ((int)date.DayOfWeek + 6) % 7 + 1;   // Monday=1 ... Sunday=7
        return version.DaysOfWeek.Contains(day);
    }

    // FindLast(EffectiveFrom <= date), как в WebAPI.
    private static ScheduleVersion? ScheduleOn(HabitSchedule snapshot, string text)
    {
        ScheduleVersion? selected = null;
        foreach (var version in snapshot.ScheduleVersions)
        {
            if (string.CompareOrdinal(version.EffectiveFrom, text) <= 0
                && (selected is null || string.CompareOrdinal(version.EffectiveFrom, selected.EffectiveFrom) >= 0))
                selected = version;
        }
        return selected;
    }
}