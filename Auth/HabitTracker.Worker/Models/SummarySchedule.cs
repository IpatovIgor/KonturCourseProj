using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HabitTracker.Worker.Models;

public sealed class SummarySchedule
{
    [BsonId]
    public required string Id { get; init; }          // = UserId

    [BsonElement("revision")]
    public long Revision { get; init; }

    [BsonElement("enabled")]
    public bool Enabled { get; init; }

    [BsonElement("localTime")]
    public required string LocalTime { get; init; }

    [BsonElement("timeZoneId")]
    public required string TimeZoneId { get; init; }

    [BsonElement("nextDueAtUtc")]
    public DateTime? NextDueAtUtc { get; init; }

    [BsonElement("nextLocalDate")]
    public string? NextLocalDate { get; init; }

    [BsonElement("lastEventId"), BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? LastEventId { get; init; }

    [BsonElement("lastCorrelationId"), BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? LastCorrelationId { get; init; }

    [BsonElement("lastTraceState")]
    public string? LastTraceState { get; init; }

    [BsonElement("lastTraceParent")]
    public string? LastTraceParent { get; init; }
}