using System.Globalization;
using System.Text.Json.Serialization;
using BackEnd.Data;
using HabitTracker.Contracts;
using MongoDB.Bson;

namespace BackEnd.Services;

public sealed record SchedulePage<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record ReminderEligibility(bool Eligible, string HabitName, long Revision)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? NextDueAtUtc { get; init; }
}

public sealed record SummaryEligibility(bool Eligible, int Planned, int Completed, int Pending, long Revision)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? NextDueAtUtc { get; init; }
}

public sealed class WorkerSchedules(WorkerScheduleRepository repository, TimeProvider clock)
{
    public async Task<SchedulePage<HabitScheduleSnapshot>> HabitPageAsync(string? after, int limit, CancellationToken ct)
    {
        var cursor = ParsePage(after, limit);
        var results = await repository.HabitPageAsync(cursor, limit + 1, ct);
        return Page(results, limit, item => item.HabitId);
    }

    public async Task<SchedulePage<SummaryScheduleSnapshot>> SummaryPageAsync(string? after, int limit, CancellationToken ct)
    {
        var cursor = ParsePage(after, limit);
        var results = await repository.SummaryPageAsync(cursor, limit + 1, ct);
        return Page(results.Select(SummaryScheduleSnapshot.From).ToList(), limit, item => item.UserId);
    }

    public async Task<ReminderEligibility> ReminderAsync(ObjectId userId, ObjectId habitId, string text, CancellationToken ct)
    {
        var date = HabitService.ParseDate(text);
        var (user, habit, completed) = await repository.ReadReminderAsync(userId, habitId, text, ct);
        if (user is null)
            throw UserNotFound();
        if (habit is null)
            throw HabitApiException.NotFound();

        var now = clock.GetUtcNow();
        var result = new ReminderEligibility(false, habit.Name, habit.Revision);
        if (!habit.ReminderEnabled || habit.Status != "active" || completed
            || date != NotificationCalendar.LocalDate(now, user.TimeZoneId) || !HabitService.IsScheduled(habit, date, text))
            return result;

        var due = DueAtUtc(date, habit.ReminderLocalTime, user.TimeZoneId);
        return now < due
            ? result with { Reason = "notDueYet", NextDueAtUtc = due }
            : result with { Eligible = true };
    }

    public async Task<SummaryEligibility> SummaryAsync(ObjectId userId, string text, CancellationToken ct)
    {
        var date = HabitService.ParseDate(text);
        var (user, habits, marks) = await repository.ReadSummaryAsync(userId, text, ct);
        if (user is null)
            throw UserNotFound();

        var planned = habits.Where(habit => HabitService.IsScheduled(habit, date, text)).ToArray();
        var completed = planned.Count(habit => marks.Contains(habit.Id));
        var result = new SummaryEligibility(false, planned.Length, completed, planned.Length - completed, user.SummaryRevision);
        var now = clock.GetUtcNow();
        if (!user.SummaryEnabled || date != NotificationCalendar.LocalDate(now, user.TimeZoneId))
            return result;

        var due = DueAtUtc(date, user.SummaryLocalTime, user.TimeZoneId);
        return now < due
            ? result with { Reason = "notDueYet", NextDueAtUtc = due }
            : result with { Eligible = true };
    }

    private static ObjectId? ParsePage(string? after, int limit)
    {
        if (limit is < 1 or > 200)
            throw HabitApiException.Validation("Размер страницы должен быть от 1 до 200");
        if (after is null)
            return null;
        return ParseId(after);
    }

    internal static ObjectId ParseId(string value) =>
        ObjectId.TryParse(value, out var id) ? id : throw HabitApiException.Validation("Некорректный ID");

    private static SchedulePage<T> Page<T>(List<T> results, int limit, Func<T, string> getId)
    {
        if (results.Count <= limit)
            return new(results, null);
        results.RemoveAt(limit);
        return new(results, getId(results[^1]));
    }

    private static DateTimeOffset DueAtUtc(DateOnly date, string time, string zone) =>
        NotificationCalendar.DueAtUtc(date, TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture), zone);

    private static HabitApiException UserNotFound() => new(404, "user_not_found", "Пользователь не найден");
}
