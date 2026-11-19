using MongoDB.Bson.Serialization.Attributes;

namespace HabitTracker.Worker.Models;

public sealed class BootstrapState
{
    public const string InitialSchedulesId = "initial-schedules-v1";

    [BsonId]
    public required string Id { get; init; }

    [BsonElement("completedAtUtc")]
    public DateTime CompletedAtUtc { get; init; }
}