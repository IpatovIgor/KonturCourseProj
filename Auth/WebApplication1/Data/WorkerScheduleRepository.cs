using BackEnd.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BackEnd.Data;

public sealed class WorkerScheduleRepository(IMongoDatabase database, IOptions<MongoDbSettings> settings)
{
    private readonly IMongoCollection<User> _users = database.GetCollection<User>(settings.Value.UsersCollectionName);
    private readonly IMongoCollection<Habit> _habits = database.GetCollection<Habit>("habits");
    private readonly IMongoCollection<HabitCompletion> _marks = database.GetCollection<HabitCompletion>("habitCompletions");

    public Task<List<User>> SummaryPageAsync(ObjectId? after, int take, CancellationToken ct) =>
        _users.Find(after is { } id ? Builders<User>.Filter.Gt(item => item.Id, id.ToString()) : Builders<User>.Filter.Empty)
            .SortBy(item => item.Id).Limit(take).ToListAsync(ct);

    public Task<List<HabitScheduleSnapshot>> HabitPageAsync(ObjectId? after, int take, CancellationToken ct) =>
        database.InSnapshotAsync(async (session, token) =>
        {
            var habits = await _habits.Find(session,
                    after is { } id ? Builders<Habit>.Filter.Gt(item => item.Id, id) : Builders<Habit>.Filter.Empty)
                .SortBy(item => item.Id).Limit(take).ToListAsync(token);
            if (habits.Count == 0)
                return [];

            var owners = habits.Select(habit => habit.UserId.ToString()).Distinct();
            var zones = await _users.Find(session, Builders<User>.Filter.In(user => user.Id, owners))
                .Project(user => new { user.Id, user.TimeZoneId }).ToListAsync(token);
            var timeZones = zones.ToDictionary(user => user.Id!, user => user.TimeZoneId);
            return habits.Select(habit => HabitScheduleSnapshot.From(habit,
                timeZones.GetValueOrDefault(habit.UserId.ToString()) ?? User.DefaultTimeZoneId)).ToList();
        }, ct);

    public Task<(User? User, Habit? Habit, bool Completed)> ReadReminderAsync(
        ObjectId owner, ObjectId id, string date, CancellationToken ct) =>
        database.InSnapshotAsync<(User?, Habit?, bool)>(async (session, token) =>
        {
            var user = await _users.Find(session, item => item.Id == owner.ToString()).FirstOrDefaultAsync(token);
            if (user is null)
                return (null, null, false);

            var habit = await _habits.Find(session, item => item.Id == id && item.UserId == owner).FirstOrDefaultAsync(token);
            if (habit is null)
                return (user, null, false);

            var mark = await _marks.Find(session, item => item.UserId == owner && item.HabitId == id && item.Date == date)
                .AnyAsync(token);
            return (user, habit, mark);
        }, ct);

    public Task<(User? User, List<Habit> Habits, HashSet<ObjectId> Completed)> ReadSummaryAsync(
        ObjectId owner, string date, CancellationToken ct) =>
        database.InSnapshotAsync<(User?, List<Habit>, HashSet<ObjectId>)>(async (session, token) =>
        {
            var user = await _users.Find(session, item => item.Id == owner.ToString()).FirstOrDefaultAsync(token);
            if (user is null)
                return (null, [], []);

            var habits = await _habits.Find(session, item => item.UserId == owner && item.Status == "active").ToListAsync(token);
            var marks = await _marks.Find(session, item => item.UserId == owner && item.Date == date)
                .Project(item => item.HabitId).ToListAsync(token);
            return (user, habits, marks.ToHashSet());
        }, ct);
}
