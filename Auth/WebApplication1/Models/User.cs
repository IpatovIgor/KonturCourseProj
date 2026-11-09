using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BackEnd.Models;

public class User
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("nickName")]
    public string NickName { get; set; } = string.Empty;

    [BsonElement("email")]
    public string Email { get; set; } = string.Empty;

    [BsonElement("passwordHash")]
    public string PasswordHash { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    public const string DefaultTimeZoneId = "Asia/Yekaterinburg";

    [BsonElement("timeZoneId")]
    public string TimeZoneId { get; set; } = DefaultTimeZoneId;

    [BsonElement("timeZoneLocked")]
    public bool TimeZoneLocked { get; set; }
}
