using System.Text.Json.Serialization;

namespace HabitTracker.Worker.Contracts;

public sealed record SchedulePage<T>([property: JsonRequired] IReadOnlyList<T> Items, [property: JsonRequired] string? NextCursor);

public sealed record HabitScheduleSnapshot(
    [property: JsonRequired] string UserId,
    [property: JsonRequired] string HabitId,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] string Status,
    [property: JsonRequired] string StartDate,
    [property: JsonRequired] ScheduleVersionSnapshot[] ScheduleVersions,
    [property: JsonRequired] string TimeZoneId,
    [property: JsonRequired] bool ReminderEnabled,
    [property: JsonRequired] string ReminderLocalTime);

public sealed record ScheduleVersionSnapshot([property: JsonRequired] string EffectiveFrom, [property: JsonRequired] int[] DaysOfWeek);

public sealed record SummaryScheduleSnapshot(
    [property: JsonRequired] string UserId,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] string LocalTime,
    [property: JsonRequired] string TimeZoneId);

public sealed record ReminderEligibility(
    [property: JsonRequired] bool Eligible,
    [property: JsonRequired] string HabitName,
    [property: JsonRequired] long Revision)
{
    public string? Reason { get; init; }
    public DateTimeOffset? NextDueAtUtc { get; init; }
}

public sealed record SummaryEligibility(
    [property: JsonRequired] bool Eligible,
    [property: JsonRequired] int Planned,
    [property: JsonRequired] int Completed,
    [property: JsonRequired] int Pending,
    [property: JsonRequired] long Revision)
{
    public string? Reason { get; init; }
    public DateTimeOffset? NextDueAtUtc { get; init; }
}