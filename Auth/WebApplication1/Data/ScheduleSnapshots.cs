using System.Text.Json.Serialization;
using BackEnd.Models;
using HabitTracker.Contracts;
using HabitTracker.Messaging;

namespace BackEnd.Data;

public sealed record HabitScheduleSnapshot(
    string UserId, string HabitId, long Revision, string Status, string StartDate,
    ScheduleVersionV1[] ScheduleVersions, string TimeZoneId, bool ReminderEnabled, string ReminderLocalTime)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReminderFirstEligibleDate { get; init; }

    public static HabitScheduleSnapshot From(Habit habit, string timeZoneId) =>
        new HabitScheduleSnapshot(habit.UserId.ToString(), habit.Id.ToString(), habit.Revision, habit.Status, habit.StartDate,
            habit.ScheduleVersions.Select(version => new ScheduleVersionV1(version.EffectiveFrom, version.DaysOfWeek))
                .ToArray(), timeZoneId, habit.ReminderEnabled, habit.ReminderLocalTime)
        { ReminderFirstEligibleDate = habit.ReminderFirstEligibleDate };

    public HabitChangedV1 AsEvent(string change, DateTime now, EventContext context) =>
        new HabitChangedV1(Guid.NewGuid(), now, context.CorrelationId, UserId, HabitId, Revision, change, Status, StartDate,
            ScheduleVersions, TimeZoneId, ReminderEnabled, ReminderLocalTime)
        { ReminderFirstEligibleDate = ReminderFirstEligibleDate };
}

public sealed record SummaryScheduleSnapshot(string UserId, long Revision, bool Enabled, string LocalTime, string TimeZoneId)
{
    public static SummaryScheduleSnapshot From(User user) =>
        new(user.Id!, user.SummaryRevision, user.SummaryEnabled, user.SummaryLocalTime, user.TimeZoneId);

    public SummarySettingsChangedV1 AsEvent(DateTime now, EventContext context) =>
        new(Guid.NewGuid(), now, context.CorrelationId, UserId, Revision, Enabled, LocalTime, TimeZoneId);
}
