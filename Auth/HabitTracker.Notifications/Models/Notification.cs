using MongoDB.Bson.Serialization.Attributes;

namespace HabitTracker.Notifications.Models;

public sealed class Notification
{
    [BsonId]
    public required string Id { get; init; }

    [BsonElement("userId")]
    public required string UserId { get; init; }

    [BsonElement("kind")]
    public required string Kind { get; init; }

    [BsonElement("localDate")]
    public required string LocalDate { get; init; }

    [BsonElement("createdAtUtc")]
    public required DateTime CreatedAtUtc { get; init; }

    [BsonElement("readAtUtc")]
    public DateTime? ReadAtUtc { get; init; }

    [BsonElement("habitId"), BsonIgnoreIfNull]
    public string? HabitId { get; init; }

    [BsonElement("habitName"), BsonIgnoreIfNull]
    public string? HabitName { get; init; }

    [BsonElement("planned"), BsonIgnoreIfNull]
    public int? Planned { get; init; }

    [BsonElement("completed"), BsonIgnoreIfNull]
    public int? Completed { get; init; }

    [BsonElement("pending"), BsonIgnoreIfNull]
    public int? Pending { get; init; }
}
