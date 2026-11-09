using BackEnd.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BackEnd.Data;

public sealed class HabitRepository(IMongoDatabase database, IOptions<MongoDbSettings> settings)
{
    private readonly IMongoCollection<User> _users = database.GetCollection<User>(settings.Value.UsersCollectionName);
    private readonly IMongoCollection<Habit> _habits = database.GetCollection<Habit>("habits");
    private readonly IMongoCollection<HabitCompletion> _marks = database.GetCollection<HabitCompletion>("habitCompletions");

    public Task<Habit> CreateAsync(ObjectId owner, Func<User?, Habit> build, CancellationToken ct) =>
        InTransactionAsync(async (session, token) =>
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
            return habit;
        }, ct);

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

    public async Task<bool> TrySaveAsync(Habit habit, CancellationToken ct)
    {
        var filter = OwnedHabit(habit.UserId, habit.Id)
            & Builders<Habit>.Filter.Eq(item => item.Revision, habit.Revision)
            & Builders<Habit>.Filter.Eq(item => item.Status, "active");
        var update = Builders<Habit>.Update.Set(item => item.ScheduleVersions, habit.ScheduleVersions)
            .Set(item => item.UpdatedAt, habit.UpdatedAt)
            .Inc(item => item.Revision, 1);
        update = habit.Status == "archived"
            ? update.Set(item => item.Status, habit.Status).Set(item => item.ArchivedAt, habit.ArchivedAt)
                .Set(item => item.ArchivedDate, habit.ArchivedDate)
            : update.Set(item => item.Name, habit.Name).Set(item => item.Description, habit.Description);
        var result = await _habits.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
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
        try
        {
            await WriteAsync();
        }
        catch (MongoWriteException exception) when (completed && exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A duplicate may not be labeled transient by MongoDB. Recheck the habit in a fresh transaction.
            await WriteAsync();
        }

        Task<bool> WriteAsync() => InTransactionAsync(async (session, token) =>
        {
            var habitFilter = OwnedHabit(owner, id);
            var habit = await _habits.Find(session, habitFilter).FirstOrDefaultAsync(token);
            var now = validate(habit);

            // Touch the habit so MongoDB detects a concurrent archive or schedule edit.
            await _habits.UpdateOneAsync(session, habitFilter, Builders<Habit>.Update.Inc(item => item.Revision, 1),
                cancellationToken: token);

            var filter = Builders<HabitCompletion>.Filter;
            var key = filter.Eq(mark => mark.UserId, owner) & filter.Eq(mark => mark.HabitId, id)
                & filter.Eq(mark => mark.Date, date);
            if (completed)
            {
                await _marks.UpdateOneAsync(session, key, Builders<HabitCompletion>.Update.SetOnInsert(mark => mark.CreatedAt, now),
                    new UpdateOptions { IsUpsert = true }, token);
            }
            else
            {
                await _marks.DeleteOneAsync(session, key, cancellationToken: token);
            }

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

    private async Task<T> InTransactionAsync<T>(
        Func<IClientSessionHandle, CancellationToken, Task<T>> write, CancellationToken ct)
    {
        using var session = await database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(write,
            new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct);
    }

    private static FilterDefinition<Habit> OwnedHabits(ObjectId owner) =>
        Builders<Habit>.Filter.Eq(habit => habit.UserId, owner);

    private static FilterDefinition<Habit> OwnedHabit(ObjectId owner, ObjectId id) =>
        OwnedHabits(owner) & Builders<Habit>.Filter.Eq(habit => habit.Id, id);

    private static readonly SortDefinition<Habit> HabitSort =
        Builders<Habit>.Sort.Descending(habit => habit.CreatedAt).Descending(habit => habit.Id);
}
