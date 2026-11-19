namespace HabitTracker.Worker.Contracts;

public sealed record SchedulePage<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record HabitScheduleSnapshot(
    string UserId,
    string HabitId,
    long Revision,
    string Status,
    string StartDate,
    ScheduleVersionSnapshot[] ScheduleVersions,
    string TimeZoneId,
    bool ReminderEnabled,
    string ReminderLocalTime);

public sealed record ScheduleVersionSnapshot(string EffectiveFrom, int[] DaysOfWeek);

public sealed record SummaryScheduleSnapshot(
    string UserId,
    long Revision,
    bool Enabled,
    string LocalTime,
    string TimeZoneId);

public sealed record ReminderEligibility(
    bool Eligible,
    string HabitName,
    long Revision)
{
    public string? Reason { get; init; }
    public DateTimeOffset? NextDueAtUtc { get; init; }
}

public sealed record SummaryEligibility(
    bool Eligible,
    int Planned,
    int Completed,
    int Pending,
    long Revision)
{
    public string? Reason { get; init; }
    public DateTimeOffset? NextDueAtUtc { get; init; }
}