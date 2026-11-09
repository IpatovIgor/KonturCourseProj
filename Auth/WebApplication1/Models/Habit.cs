using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BackEnd.Models;

public sealed class Habit
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("userId")]
    public ObjectId UserId { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("startDate")]
    public string StartDate { get; set; } = "";

    [BsonElement("scheduleVersions")]
    public List<HabitSchedule> ScheduleVersions { get; set; } = [];

    [BsonElement("status")]
    public string Status { get; set; } = "active";

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [BsonElement("archivedAt")]
    public DateTime? ArchivedAt { get; set; }

    [BsonElement("archivedDate")]
    public string? ArchivedDate { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }
}

public sealed record HabitSchedule(
    [property: BsonElement("effectiveFrom")] string EffectiveFrom,
    [property: BsonElement("daysOfWeek")] int[] DaysOfWeek);

public sealed class HabitCompletion
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("userId")]
    public ObjectId UserId { get; set; }

    [BsonElement("habitId")]
    public ObjectId HabitId { get; set; }

    [BsonElement("date")]
    public string Date { get; set; } = "";

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }
}
