namespace HabitTracker.Contracts;

public interface IVersionedEvent
{
    Guid EventId { get; }
    DateTime OccurredAtUtc { get; }
    Guid CorrelationId { get; }
}

public sealed record ScheduleVersionV1(string EffectiveFrom, int[] DaysOfWeek);

public sealed record HabitChangedV1(
    Guid EventId, DateTime OccurredAtUtc, Guid CorrelationId, string UserId, string HabitId, long Revision,
    string Change, string Status, string StartDate, ScheduleVersionV1[] ScheduleVersions,
    string TimeZoneId, bool ReminderEnabled, string ReminderLocalTime) : IVersionedEvent;

public sealed record SummarySettingsChangedV1(
    Guid EventId, DateTime OccurredAtUtc, Guid CorrelationId, string UserId, long Revision, bool Enabled,
    string LocalTime, string TimeZoneId) : IVersionedEvent;

public sealed record NotificationReadyV1(
    Guid EventId, DateTime OccurredAtUtc, Guid CorrelationId, Guid NotificationId, string UserId, string Kind,
    string LocalDate, DateTime CreatedAtUtc, string? HabitId = null, string? HabitName = null,
    int? Planned = null, int? Completed = null, int? Pending = null) : IVersionedEvent;
