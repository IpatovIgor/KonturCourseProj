using System.Text.Json;
using HabitTracker.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization.Attributes;

namespace HabitTracker.Messaging;

public sealed class OutboxEvent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonWriterSettings BsonJson = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    [BsonId, BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; init; }
    [BsonElement("eventId"), BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid EventId { get; init; }
    [BsonElement("contract")]
    public required string Contract { get; init; }
    [BsonElement("payload")]
    public required BsonDocument Payload { get; init; }
    [BsonElement("headers")]
    public required BsonDocument Headers { get; init; }
    [BsonElement("status")]
    public string Status { get; init; } = "pending";
    [BsonElement("attempts")]
    public int Attempts { get; init; }
    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }
    [BsonElement("lastUpdated")]
    public DateTime LastUpdated { get; init; }
    [BsonElement("nextAttemptAt")]
    public DateTime NextAttemptAt { get; init; }
    [BsonElement("leaseOwner")]
    public string? LeaseOwner { get; init; }
    [BsonElement("leaseUntil")]
    public DateTime? LeaseUntil { get; init; }
    [BsonElement("leaseVersion")]
    public long LeaseVersion { get; init; }
    [BsonElement("publishedAt")]
    public DateTime? PublishedAt { get; init; }

    public static OutboxEvent Create<T>(T message, EventContext context) where T : class, IVersionedEvent
    {
        var headers = new BsonDocument("correlationId", message.CorrelationId.ToString());
        if (context.TraceParent is { } parent)
            headers.Add("traceparent", parent);
        if (context.TraceState is { } state)
            headers.Add("tracestate", state);

        return new()
        {
            Id = message.EventId,
            EventId = message.EventId,
            Contract = typeof(T).Name,
            Payload = BsonDocument.Parse(JsonSerializer.Serialize(message, JsonOptions)),
            Headers = headers,
            CreatedAt = message.OccurredAtUtc,
            LastUpdated = message.OccurredAtUtc,
            NextAttemptAt = message.OccurredAtUtc
        };
    }

    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload.ToJson(BsonJson), JsonOptions)
        ?? throw new InvalidOperationException("Outbox payload is missing.");

    public EventContext Context => new(Guid.Parse(Headers["correlationId"].AsString),
        (Headers.GetValue("traceparent", BsonNull.Value) as BsonString)?.Value,
        (Headers.GetValue("tracestate", BsonNull.Value) as BsonString)?.Value);
}
