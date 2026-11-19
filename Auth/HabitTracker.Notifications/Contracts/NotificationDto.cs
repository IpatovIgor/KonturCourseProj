using System.Text.Json.Serialization;
using HabitTracker.Notifications.Models;

namespace HabitTracker.Notifications.Contracts;

public sealed record NotificationDto(
    string Id, string Kind, string LocalDate, DateTime CreatedAtUtc, DateTime? ReadAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HabitId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HabitName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Planned,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Completed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Pending)
{
    public static NotificationDto From(Notification value) => new(
        value.Id, value.Kind, value.LocalDate, value.CreatedAtUtc, value.ReadAtUtc,
        value.HabitId, value.HabitName, value.Planned, value.Completed, value.Pending);
}

public sealed record NotificationPage(IReadOnlyList<NotificationDto> Items, string? NextCursor, long UnreadCount);
