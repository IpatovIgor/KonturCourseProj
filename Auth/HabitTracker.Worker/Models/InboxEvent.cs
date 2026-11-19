using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HabitTracker.Worker.Models;

public sealed class InboxEvent
{
    [BsonId, BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; init; }                     // = EventId

    [BsonElement("eventType")]
    public required string EventType { get; init; }

    [BsonElement("processedAtUtc")]
    public DateTime ProcessedAtUtc { get; init; }
}