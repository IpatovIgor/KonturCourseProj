using System.Text.Json.Serialization;

namespace BackEnd.Models;

public sealed record HabitRequest(string? Name, string? Description,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int[]? DaysOfWeek);

public record HabitResponse(
    string Id, string Name, string? Description, int[] DaysOfWeek,
    ScheduleResponse? NextSchedule, string StartDate, DateTime CreatedAt,
    DateTime UpdatedAt, DateTime? ArchivedAt, string? ArchivedDate);

public sealed record ScheduleResponse(string EffectiveFrom, int[] DaysOfWeek);

public sealed record TodayHabitResponse : HabitResponse
{
    public TodayHabitResponse(HabitResponse habit, bool completed) : base(habit)
    {
        Completed = completed;
    }

    public bool Completed { get; }
}

public sealed record HabitPage(IReadOnlyList<HabitResponse> Items, int Page, int PageSize, long TotalCount);

public sealed record TodayHabitsResponse(string Date, string TimeZoneId, IReadOnlyList<TodayHabitResponse> Items);

public sealed record HabitDay(string Date, string Status);

public sealed record HabitHistoryResponse(string HabitId, string From, string To, IReadOnlyList<HabitDay> Days);

public sealed record HabitStatisticsResponse(
    string HabitId, string From, string To, int PlannedDays, int CompletedDays, int MissedDays,
    int PendingDays, decimal? CompletionRatePercent, int CurrentStreak, int LongestStreak);
