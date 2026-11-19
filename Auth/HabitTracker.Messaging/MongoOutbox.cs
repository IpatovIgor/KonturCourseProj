using HabitTracker.Contracts;
using MongoDB.Driver;

namespace HabitTracker.Messaging;

public sealed class MongoOutbox(IMongoDatabase database)
{
    private readonly IMongoCollection<OutboxEvent> _events = database.GetCollection<OutboxEvent>("outboxEvents");
    private static readonly FilterDefinitionBuilder<OutboxEvent> Filter = Builders<OutboxEvent>.Filter;
    private static readonly UpdateDefinitionBuilder<OutboxEvent> Update = Builders<OutboxEvent>.Update;

    public Task InsertAsync<T>(IClientSessionHandle session, T message, EventContext context, CancellationToken ct)
        where T : class, IVersionedEvent =>
        _events.InsertOneAsync(session, OutboxEvent.Create(message, context), cancellationToken: ct);

    public async Task<OutboxEvent?> ClaimAsync(string owner, DateTime now, CancellationToken ct)
    {
        var update = Update.Set(item => item.Status, "publishing")
            .Set(item => item.LeaseOwner, owner).Set(item => item.LeaseUntil, now.AddMinutes(1))
            .Set(item => item.LastUpdated, now).Inc(item => item.LeaseVersion, 1).Inc(item => item.Attempts, 1);
        var sort = Builders<OutboxEvent>.Sort;
        return await ClaimAsync(Filter.Eq(item => item.Status, "publishing") & Filter.Lte(item => item.LeaseUntil, now),
                sort.Ascending(item => item.LeaseUntil).Ascending(item => item.Id))
            ?? await ClaimAsync(Filter.Eq(item => item.Status, "pending") & Filter.Lte(item => item.NextAttemptAt, now),
                sort.Ascending(item => item.NextAttemptAt).Ascending(item => item.Id));

        Task<OutboxEvent> ClaimAsync(FilterDefinition<OutboxEvent> filter, SortDefinition<OutboxEvent> order) =>
            _events.FindOneAndUpdateAsync(filter, update, new()
            {
                Sort = order,
                ReturnDocument = ReturnDocument.After
            }, ct);
    }

    public Task PublishedAsync(OutboxEvent item, DateTime now, CancellationToken ct) =>
        _events.UpdateOneAsync(OwnedLease(item, now), Release("published", now)
            .Set(entry => entry.PublishedAt, now), cancellationToken: ct);

    public Task RetryAsync(OutboxEvent item, DateTime now, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(item.Attempts - 1, 9))));
        return _events.UpdateOneAsync(OwnedLease(item, now), Release("pending", now)
            .Set(entry => entry.NextAttemptAt, now + delay), cancellationToken: ct);
    }

    private static FilterDefinition<OutboxEvent> OwnedLease(OutboxEvent item, DateTime now) =>
        Filter.Eq(entry => entry.Id, item.Id) & Filter.Eq(entry => entry.Status, "publishing")
        & Filter.Eq(entry => entry.LeaseOwner, item.LeaseOwner) & Filter.Eq(entry => entry.LeaseVersion, item.LeaseVersion)
        & Filter.Gt(entry => entry.LeaseUntil, now);

    private static UpdateDefinition<OutboxEvent> Release(string status, DateTime now) =>
        Update.Set(entry => entry.Status, status).Set(entry => entry.LastUpdated, now)
            .Unset(entry => entry.LeaseOwner).Unset(entry => entry.LeaseUntil);

    public Task<DateTime?> OldestUnpublishedAsync(CancellationToken ct) =>
        _events.Find(Filter.In(item => item.Status, ["pending", "publishing"]))
            .SortBy(item => item.CreatedAt).Limit(1).Project(item => (DateTime?)item.CreatedAt).FirstOrDefaultAsync(ct);

    public Task EnsureIndexesAsync(CancellationToken ct)
    {
        var keys = Builders<OutboxEvent>.IndexKeys;
        return _events.Indexes.CreateManyAsync([
            new(keys.Ascending(item => item.Status).Ascending(item => item.NextAttemptAt).Ascending(item => item.Id)),
            new(keys.Ascending(item => item.Status).Ascending(item => item.LeaseUntil).Ascending(item => item.Id)),
            new(keys.Ascending(item => item.Status).Ascending(item => item.CreatedAt)),
            new(keys.Ascending(item => item.PublishedAt), new CreateIndexOptions<OutboxEvent>
            {
                ExpireAfter = TimeSpan.FromDays(30),
                PartialFilterExpression = Filter.Eq(item => item.Status, "published")
            })
        ], ct);
    }
}
