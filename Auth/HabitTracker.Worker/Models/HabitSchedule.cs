using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HabitTracker.Worker.Models;

public sealed class HabitSchedule
{
    [BsonId]
    public required string Id { get; init; }          // = HabitId

    [BsonElement("userId")]
    public required string UserId { get; init; }

    [BsonElement("revision")]
    public long Revision { get; init; }

    [BsonElement("status")]
    public required string Status { get; init; }

    [BsonElement("startDate")]
    public required string StartDate { get; init; }

    [BsonElement("scheduleVersions")]
    public required IReadOnlyList<ScheduleVersion> ScheduleVersions { get; init; }

    [BsonElement("timeZoneId")]
    public required string TimeZoneId { get; init; }

    [BsonElement("reminderEnabled")]
    public bool ReminderEnabled { get; init; }

    [BsonElement("reminderLocalTime")]
    public required string ReminderLocalTime { get; init; }

    [BsonElement("nextDueAtUtc")]
    public DateTime? NextDueAtUtc { get; init; }

    [BsonElement("nextLocalDate")]
    public string? NextLocalDate { get; init; }

    [BsonElement("lastEventId"), BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? LastEventId { get; init; }

    [BsonElement("lastCorrelationId"), BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? LastCorrelationId { get; init; }

    [BsonElement("lastTraceParent")]
    public string? LastTraceParent { get; init; }
}

public sealed class ScheduleVersion
{
    [BsonElement("effectiveFrom")]
    public required string EffectiveFrom { get; init; }

    [BsonElement("daysOfWeek")]
    public required int[] DaysOfWeek { get; init; }
}