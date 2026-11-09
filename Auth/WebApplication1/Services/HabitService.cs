using System.Globalization;
using BackEnd.Data;
using BackEnd.Models;
using MongoDB.Bson;

namespace BackEnd.Services;

public sealed class HabitService(HabitRepository repository, IUserRepository users, TimeProvider clock)
{
    public async Task<User> GetOwnerAsync(string? subject, CancellationToken ct)
    {
        if (!ObjectId.TryParse(subject, out var owner))
            throw HabitApiException.Unauthenticated();

        return await users.GetByIdAsync(owner.ToString(), ct) ?? throw HabitApiException.Unauthenticated();
    }

    public async Task<HabitResponse> CreateAsync(User owner, HabitRequest request, CancellationToken ct)
    {
        var fields = Validate(request);
        var habit = await repository.CreateAsync(OwnerId(owner), profile =>
        {
            var now = clock.GetUtcNow();
            var timestamp = ToStoredTimestamp(now);
            var start = Format(LocalDate(profile ?? throw HabitApiException.Unauthenticated(), now));
            return new Habit
            {
                Id = ObjectId.GenerateNewId(),
                UserId = OwnerId(owner),
                Name = fields.Name,
                Description = fields.Description,
                StartDate = start,
                ScheduleVersions = [new(start, fields.Days)],
                CreatedAt = timestamp,
                UpdatedAt = timestamp
            };
        }, ct);
        return ToResponse(habit, habit.StartDate);
    }

    public async Task<HabitPage> ListAsync(User owner, string status, int page, int pageSize, CancellationToken ct)
    {
        if (status is not ("active" or "archived") || page < 1 || pageSize is < 1 or > 100)
            throw HabitApiException.Validation("Некорректные параметры списка привычек");

        var today = Format(LocalDate(owner, clock.GetUtcNow()));
        var (items, total) = await repository.GetPageAsync(OwnerId(owner), status, page, pageSize, ct);
        return new(items.Select(habit => ToResponse(habit, today)).ToArray(), page, pageSize, total);
    }

    public async Task<HabitResponse> GetAsync(User owner, ObjectId id, CancellationToken ct)
    {
        var today = Format(LocalDate(owner, clock.GetUtcNow()));
        return ToResponse(await GetOwnedHabitAsync(owner, id, ct), today);
    }

    public async Task<HabitResponse> EditAsync(User owner, ObjectId id, HabitRequest request, CancellationToken ct)
    {
        var fields = Validate(request);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var habit = await GetOwnedHabitAsync(owner, id, ct);
            RequireActive(habit);
            var now = clock.GetUtcNow();
            var today = LocalDate(owner, now);
            var todayText = Format(today);

            habit.Name = fields.Name;
            habit.Description = fields.Description;
            habit.UpdatedAt = ToStoredTimestamp(now);
            var current = ScheduleOn(habit, todayText);
            habit.ScheduleVersions.RemoveAll(version => Compare(version.EffectiveFrom, todayText) > 0);
            if (!current.DaysOfWeek.SequenceEqual(fields.Days))
                habit.ScheduleVersions.Add(new(Format(today.AddDays(1)), fields.Days));

            if (await repository.TrySaveAsync(habit, ct))
                return ToResponse(habit, todayText);
        }
    }

    public async Task ArchiveAsync(User owner, ObjectId id, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var habit = await GetOwnedHabitAsync(owner, id, ct);
            if (habit.Status == "archived")
                return;

            var now = clock.GetUtcNow();
            var timestamp = ToStoredTimestamp(now);
            var date = Format(LocalDate(owner, now));
            habit.Status = "archived";
            habit.ArchivedAt = timestamp;
            habit.ArchivedDate = date;
            habit.UpdatedAt = timestamp;
            habit.ScheduleVersions.RemoveAll(version => Compare(version.EffectiveFrom, date) > 0);
            if (await repository.TrySaveAsync(habit, ct))
                return;
        }
    }

    public async Task<TodayHabitsResponse> TodayAsync(User owner, CancellationToken ct)
    {
        var today = LocalDate(owner, clock.GetUtcNow());
        var date = Format(today);
        var ownerId = OwnerId(owner);
        var habits = await repository.GetActiveAsync(ownerId, ct);
        var marks = await repository.GetCompletionsAsync(ownerId, null, date, date, ct);
        var completed = marks.Select(mark => mark.HabitId).ToHashSet();
        var items = habits.Where(habit => IsScheduled(habit, today, date))
            .Select(habit => new TodayHabitResponse(ToResponse(habit, date), completed.Contains(habit.Id)))
            .ToArray();
        return new(date, owner.TimeZoneId, items);
    }

    public Task SetCompletionAsync(User owner, ObjectId id, string value, bool completed, CancellationToken ct)
    {
        var date = ParseDate(value);
        return repository.SetCompletionAsync(OwnerId(owner), id, value, completed, habit =>
        {
            if (habit is null)
                throw HabitApiException.NotFound();
            RequireActive(habit);

            var now = clock.GetUtcNow();
            if (date > LocalDate(owner, now) || !IsScheduled(habit, date, value))
                throw new HabitApiException(400, "invalid_completion_date", "На эту дату нельзя изменить отметку");
            return ToStoredTimestamp(now);
        }, ct);
    }

    public async Task<HabitHistoryResponse> HistoryAsync(
        User owner, ObjectId id, string? from, string? to, CancellationToken ct)
    {
        var today = LocalDate(owner, clock.GetUtcNow());
        var range = ParseRange(from, to, today);
        var habit = await GetOwnedHabitAsync(owner, id, ct);
        var marks = await ReadMarksAsync(owner, habit, range.From, range.To, ct);
        return new(id.ToString(), Format(range.From), Format(range.To),
            Calendar(habit, range.From, range.To, today, marks).Select(day => day.Entry).ToArray());
    }

    public async Task<HabitStatisticsResponse> StatisticsAsync(
        User owner, ObjectId id, string? from, string? to, CancellationToken ct)
    {
        var today = LocalDate(owner, clock.GetUtcNow());
        var range = ParseRange(from, to, today);
        var habit = await GetOwnedHabitAsync(owner, id, ct);
        var start = ParseDate(habit.StartDate);
        var marks = await ReadMarksAsync(owner, habit, start, today, ct);
        int completed = 0, missed = 0, pending = 0, streak = 0, longest = 0;
        foreach (var (date, entry) in Calendar(habit, start, today, today, marks))
        {
            ct.ThrowIfCancellationRequested();
            var inRange = date >= range.From && date <= range.To;
            switch (entry.Status)
            {
                case "completed":
                    longest = Math.Max(longest, ++streak);
                    if (inRange) completed++;
                    break;
                case "missed":
                    streak = 0;
                    if (inRange) missed++;
                    break;
                case "pending":
                    if (inRange) pending++;
                    break;
            }
        }

        var planned = completed + missed + pending;
        decimal? rate = planned == 0 ? null : Math.Round(100m * completed / planned, 2, MidpointRounding.AwayFromZero);
        return new(id.ToString(), Format(range.From), Format(range.To), planned, completed, missed, pending,
            rate, habit.Status == "archived" ? 0 : streak, longest);
    }

    private async Task<Habit> GetOwnedHabitAsync(User owner, ObjectId id, CancellationToken ct) =>
        await repository.GetAsync(OwnerId(owner), id, ct) ?? throw HabitApiException.NotFound();

    private async Task<HashSet<string>> ReadMarksAsync(User owner, Habit habit, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var marks = await repository.GetCompletionsAsync(OwnerId(owner), habit.Id, Format(from), Format(to), ct);
        return marks.Select(mark => mark.Date).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(DateOnly Date, HabitDay Entry)> Calendar(
        Habit habit, DateOnly from, DateOnly to, DateOnly today, HashSet<string> marks)
    {
        var first = Math.Max(from.DayNumber, ParseDate(habit.StartDate).DayNumber);
        var last = habit.ArchivedDate is { } archived
            ? Math.Min(to.DayNumber, ParseDate(archived).DayNumber)
            : to.DayNumber;
        for (var day = first; day <= last; day++)
        {
            var date = DateOnly.FromDayNumber(day);
            var text = Format(date);
            var status = !IsScheduled(habit, date, text) ? "notScheduled"
                : marks.Contains(text) ? "completed"
                : date == today ? "pending" : "missed";
            yield return (date, new HabitDay(text, status));
        }
    }

    private static HabitResponse ToResponse(Habit habit, string today)
    {
        var current = ScheduleOn(habit, habit.ArchivedDate ?? today);
        var future = habit.Status == "active"
            ? habit.ScheduleVersions.Find(version => Compare(version.EffectiveFrom, today) > 0)
            : null;
        var next = future is null ? null : new ScheduleResponse(future.EffectiveFrom, future.DaysOfWeek);
        return new(habit.Id.ToString(), habit.Name, habit.Description, current.DaysOfWeek, next,
            habit.StartDate, habit.CreatedAt, habit.UpdatedAt, habit.ArchivedAt, habit.ArchivedDate);
    }

    private static (string Name, string? Description, int[] Days) Validate(HabitRequest request)
    {
        var name = request.Name?.Trim();
        var description = request.Description?.Trim();
        var days = request.DaysOfWeek;
        if (name is null || name.Length is < 1 or > 100)
            throw HabitApiException.Validation("Название должно содержать от 1 до 100 символов");
        if (description?.Length > 1000)
            throw HabitApiException.Validation("Описание должно содержать не более 1000 символов");
        if (days is null || days.Length is < 1 or > 7 || days.Any(day => day is < 1 or > 7)
            || days.Distinct().Count() != days.Length)
            throw HabitApiException.Validation("Дни недели должны быть уникальными числами от 1 до 7");
        return (name, description is "" ? null : description, days.Order().ToArray());
    }

    private static (DateOnly From, DateOnly To) ParseRange(string? from, string? to, DateOnly today)
    {
        if (from is null && to is null)
            return (today.AddDays(-29), today);
        if (from is null || to is null)
            throw HabitApiException.Validation("Укажите обе границы периода");

        var first = ParseDate(from);
        var last = ParseDate(to);
        if (last < first || last > today || last.DayNumber - first.DayNumber >= 366)
            throw HabitApiException.Validation("Период должен завершаться не позже сегодня и содержать до 366 дней");
        return (first, last);
    }

    private static bool IsScheduled(Habit habit, DateOnly date, string text) =>
        Compare(text, habit.StartDate) >= 0 && ScheduleOn(habit, text).DaysOfWeek.Contains(((int)date.DayOfWeek + 6) % 7 + 1);

    private static HabitSchedule ScheduleOn(Habit habit, string date) =>
        habit.ScheduleVersions.FindLast(version => Compare(version.EffectiveFrom, date) <= 0) ?? habit.ScheduleVersions[0];

    private static void RequireActive(Habit habit)
    {
        if (habit.Status == "archived")
            throw HabitApiException.Archived();
    }

    private static int Compare(string left, string right) => string.CompareOrdinal(left, right);

    private static ObjectId OwnerId(User user) => ObjectId.Parse(user.Id);

    private static DateTime ToStoredTimestamp(DateTimeOffset now) =>
        DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()).UtcDateTime;

    private static DateOnly LocalDate(User user, DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId)).DateTime);

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw HabitApiException.Validation("Дата должна быть в формате YYYY-MM-DD");

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
