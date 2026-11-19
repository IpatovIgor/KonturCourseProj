using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HabitTracker.Worker.Models;

public static class JobStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public static class JobKind
{
    public const string HabitReminder = "habitReminder";
    public const string DailySummary = "dailySummary";
}

public sealed class Job
{
    [BsonId, BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; init; }

    [BsonElement("kind")]
    public required string Kind { get; init; }

    [BsonElement("userId")]
    public required string UserId { get; init; }

    [BsonElement("habitKey")]
    public required string HabitKey { get; init; }    // habitId или "" для сводки

    [BsonElement("localDate")]
    public required string LocalDate { get; init; }   // YYYY-MM-DD

    [BsonElement("dueAtUtc")]
    public DateTime DueAtUtc { get; init; }

    [BsonElement("timeZoneId")]
    public required string TimeZoneId { get; init; }

    [BsonElement("status")]
    public string Status { get; set; } = JobStatus.Pending;

    [BsonElement("attempts")]
    public int Attempts { get; set; }

    [BsonElement("httpFailureCount")]
    public int HttpFailureCount { get; set; }

    [BsonElement("leaseOwner")]
    public string? LeaseOwner { get; set; }

    [BsonElement("leaseUntil")]
    public DateTime? LeaseUntil { get; set; }

    [BsonElement("leaseVersion")]
    public int LeaseVersion { get; set; }

    [BsonElement("lastErrorCode")]
    public string? LastErrorCode { get; set; }

    [BsonElement("lastCorrelationId")]
    public Guid? LastCorrelationId { get; set; }

    [BsonElement("lastTraceParent")]
    public string? LastTraceParent { get; set; }

    [BsonElement("lastUpdatedAtUtc")]
    public DateTime LastUpdatedAtUtc { get; set; }
}