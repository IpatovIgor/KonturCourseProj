using BackEnd.Models;
using BackEnd.Services;
using HabitTracker.Messaging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BackEnd.Data;

public sealed class HabitRepository(IMongoDatabase database, IOptions<MongoDbSettings> settings,
    MongoOutbox outbox, TimeProvider clock, RequestEventContext events,
    IOptions<NotificationFeatures> features) : IHabitRepository
{
    private readonly IMongoCollection<User> _users = database.GetCollection<User>(settings.Value.UsersCollectionName);
    private readonly IMongoCollection<Habit> _habits = database.GetCollection<Habit>("habits");
    private readonly IMongoCollection<HabitCompletion> _marks = database.GetCollection<HabitCompletion>("habitCompletions");

    public Task<Habit> CreateAsync(ObjectId owner, Func<User?, Habit> build, CancellationToken ct)
    {
        var context = events.Capture();
        return database.InSnapshotAsync(async (session, token) =>
        {
            var filter = Builders<User>.Filter.Eq(user => user.Id, owner.ToString());
            var user = await _users.Find(session, filter).FirstOrDefaultAsync(token);
            var habit = build(user);
            if (user is { TimeZoneLocked: false })
            {
                var update = Builders<User>.Update.Set(profile => profile.TimeZoneId, user.TimeZoneId)
                    .Set(profile => profile.TimeZoneLocked, true);
                await _users.UpdateOneAsync(session, filter, update, cancellationToken: token);
            }

            await _habits.InsertOneAsync(session, habit, cancellationToken: token);
            if (features.Value.NotificationsEnabled)
                await AddEventAsync(session, habit, user?.TimeZoneId ?? User.DefaultTimeZoneId, "created", context, token);
            return habit;
        }, ct);
    }

    public async Task<Habit?> GetAsync(ObjectId owner, ObjectId id, CancellationToken ct) =>
        await _habits.Find(OwnedHabit(owner, id)).FirstOrDefaultAsync(ct);

    public async Task<(List<Habit> Items, long Total)> GetPageAsync(
        ObjectId owner, string status, int page, int pageSize, CancellationToken ct)
    {
        var filter = OwnedHabits(owner) & Builders<Habit>.Filter.Eq(habit => habit.Status, status);
        var offset = ((long)page - 1) * pageSize;
        var pipeline = new EmptyPipelineDefinition<Habit>();
        var itemsFacet = AggregateFacet.Create("items", pipeline.Skip(offset).Limit(pageSize));
        var totalFacet = AggregateFacet.Create("total", pipeline.Count());

        var result = await _habits.Aggregate().Match(filter).Sort(HabitSort)
            .Facet(itemsFacet, totalFacet).SingleAsync(ct);
        var items = result.Facets.Single(facet => facet.Name == itemsFacet.Name).Output<Habit>();
        var totals = result.Facets.Single(facet => facet.Name == totalFacet.Name).Output<AggregateCountResult>();
        return (items.ToList(), totals.SingleOrDefault()?.Count ?? 0);
    }

    public Task<List<Habit>> GetActiveAsync(ObjectId owner, CancellationToken ct) =>
        _habits.Find(OwnedHabits(owner) & Builders<Habit>.Filter.Eq(habit => habit.Status, "active"))
            .Sort(HabitSort).ToListAsync(ct);

    public Task<bool> TrySaveAsync(Habit habit, CancellationToken ct)
    {
        var filters = Builders<Habit>.Filter;
        var revision = filters.Eq(item => item.Revision, habit.Revision);
        if (habit.Revision == 0)
            revision |= filters.Exists(item => item.Revision, false);

        var filter = OwnedHabit(habit.UserId, habit.Id) & revision & filters.Eq(item => item.Status, "active");
        var update = Builders<Habit>.Update.Set(item => item.ScheduleVersions, habit.ScheduleVersions)
            .Set(item => item.UpdatedAt, habit.UpdatedAt)
            .Inc(item => item.Revision, 1);
        update = habit.Status == "archived"
            ? update.Set(item => item.Status, habit.Status).Set(item => item.ArchivedAt, habit.ArchivedAt)
                .Set(item => item.ArchivedDate, habit.ArchivedDate)
            : update.Set(item => item.Name, habit.Name).Set(item => item.Description, habit.Description);
        var change = habit.Status == "archived" ? "archived" : "edited";
        var context = events.Capture();
        return database.InSnapshotAsync(async (session, token) =>
        {
            var saved = await _habits.FindOneAndUpdateAsync(session, filter, update,
                new FindOneAndUpdateOptions<Habit> { ReturnDocument = ReturnDocument.After }, token);
            if (saved is null)
                return false;

            await AddEventAsync(session, saved, change, context, token);
            return true;
        }, ct);
    }

    public Task<Habit?> SaveReminderAsync(ObjectId owner, ObjectId id, NotificationSettings settings, CancellationToken ct)
    {
        var context = events.Capture();
        return database.InSnapshotAsync<Habit?>(async (session, token) =>
        {
            var filter = OwnedHabit(owner, id);
            var habit = await _habits.Find(session, filter).FirstOrDefaultAsync(token);
            if (habit is null)
                return null;
            if (habit.Status == "archived")
                throw HabitApiException.Archived();
            if (habit.ReminderEnabled == settings.Enabled && habit.ReminderLocalTime == settings.LocalTime)
                return habit;

            var user = await _users.Find(session, item => item.Id == owner.ToString()).FirstOrDefaultAsync(token);
            var zone = user?.TimeZoneId ?? User.DefaultTimeZoneId;
            var now = clock.GetUtcNow();
            var update = Builders<Habit>.Update.Set(item => item.ReminderEnabled, settings.Enabled)
                .Set(item => item.ReminderLocalTime, settings.LocalTime)
                .Set(item => item.ReminderFirstEligibleDate, settings.FirstEligibleDate(now, zone))
                .Set(item => item.UpdatedAt, now.UtcDateTime)
                .Inc(item => item.Revision, 1);
            var saved = await _habits.FindOneAndUpdateAsync(session, filter, update,
                new FindOneAndUpdateOptions<Habit> { ReturnDocument = ReturnDocument.After }, token);
            if (features.Value.NotificationsEnabled)
                await AddEventAsync(session, saved, zone, "reminderChanged", context, token);
            return saved;
        }, ct);
    }

    public Task<List<HabitCompletion>> GetCompletionsAsync(
        ObjectId owner, ObjectId? habitId, string from, string to, CancellationToken ct)
    {
        var filter = Builders<HabitCompletion>.Filter;
        var query = filter.Eq(mark => mark.UserId, owner) & filter.Gte(mark => mark.Date, from)
            & filter.Lte(mark => mark.Date, to);
        if (habitId is { } id)
            query &= filter.Eq(mark => mark.HabitId, id);
        return _marks.Find(query).ToListAsync(ct);
    }

    public async Task SetCompletionAsync(ObjectId owner, ObjectId id, string date, bool completed,
        Func<Habit?, DateTime> validate, CancellationToken ct)
    {
        var context = events.Capture();
        try
        {
            await WriteAsync();
        }
        catch (MongoWriteException exception) when (completed && exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A duplicate may not be labeled transient by MongoDB. Recheck the habit in a fresh transaction.
            await WriteAsync();
        }

        Task<bool> WriteAsync() => database.InSnapshotAsync(async (session, token) =>
        {
            var habitFilter = OwnedHabit(owner, id);
            var habit = await _habits.Find(session, habitFilter).FirstOrDefaultAsync(token);
            var now = validate(habit);

            var filter = Builders<HabitCompletion>.Filter;
            var key = filter.Eq(mark => mark.UserId, owner) & filter.Eq(mark => mark.HabitId, id)
                & filter.Eq(mark => mark.Date, date);
            var changed = completed
                ? (await _marks.UpdateOneAsync(session, key,
                    Builders<HabitCompletion>.Update.SetOnInsert(mark => mark.CreatedAt, now),
                    new UpdateOptions { IsUpsert = true }, token)).UpsertedId is not null
                : (await _marks.DeleteOneAsync(session, key, cancellationToken: token)).DeletedCount == 1;
            if (!changed)
                return false;

            // Writing the habit makes concurrent archive/edit conflict with this mark transaction.
            var saved = await _habits.FindOneAndUpdateAsync(session, habitFilter,
                Builders<Habit>.Update.Inc(item => item.Revision, 1),
                new FindOneAndUpdateOptions<Habit> { ReturnDocument = ReturnDocument.After }, token);
            await AddEventAsync(session, saved, "completionChanged", context, token);
            return true;
        }, ct);
    }

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await _habits.Indexes.CreateOneAsync(new CreateIndexModel<Habit>(
            Builders<Habit>.IndexKeys.Ascending(item => item.UserId).Ascending(item => item.Status)
                .Descending(item => item.CreatedAt).Descending(item => item.Id)), cancellationToken: ct);
        await _marks.Indexes.CreateManyAsync([
            new CreateIndexModel<HabitCompletion>(
                Builders<HabitCompletion>.IndexKeys.Ascending(mark => mark.UserId).Ascending(mark => mark.HabitId)
                    .Ascending(mark => mark.Date), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<HabitCompletion>(
                Builders<HabitCompletion>.IndexKeys.Ascending(mark => mark.UserId).Ascending(mark => mark.Date))
        ], ct);
    }

    private async Task AddEventAsync(IClientSessionHandle session, Habit habit, string change,
        EventContext context, CancellationToken ct)
    {
        if (!features.Value.NotificationsEnabled)
            return;

        var user = await _users.Find(session, item => item.Id == habit.UserId.ToString()).FirstOrDefaultAsync(ct);
        await AddEventAsync(session, habit, user?.TimeZoneId ?? User.DefaultTimeZoneId, change, context, ct);
    }

    private Task AddEventAsync(IClientSessionHandle session, Habit habit, string zone, string change,
        EventContext context, CancellationToken ct) => outbox.InsertAsync(session,
        HabitScheduleSnapshot.From(habit, zone).AsEvent(change, clock.GetUtcNow().UtcDateTime, context), context, ct);

    private static FilterDefinition<Habit> OwnedHabits(ObjectId owner) =>
        Builders<Habit>.Filter.Eq(habit => habit.UserId, owner);

    private static FilterDefinition<Habit> OwnedHabit(ObjectId owner, ObjectId id) =>
        OwnedHabits(owner) & Builders<Habit>.Filter.Eq(habit => habit.Id, id);

    private static readonly SortDefinition<Habit> HabitSort =
        Builders<Habit>.Sort.Descending(habit => habit.CreatedAt).Descending(habit => habit.Id);
}
