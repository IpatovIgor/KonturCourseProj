using HabitTracker.Notifications.Consumers;
using HabitTracker.Notifications.Contracts;
using HabitTracker.Notifications.Models;
using HabitTracker.Notifications.Services;
using MongoDB.Driver;

namespace HabitTracker.Notifications.Data;

public sealed class NotificationRepository(IMongoDatabase database, TimeProvider clock)
{
    private static readonly FilterDefinitionBuilder<Notification> Filters = Builders<Notification>.Filter;
    private static readonly SortDefinition<Notification> ByNewest =
        Builders<Notification>.Sort.Descending(item => item.CreatedAtUtc).Descending(item => item.Id);
    private readonly IMongoCollection<Notification> _collection = database.GetCollection<Notification>("notifications");

    public Task EnsureIndexesAsync(CancellationToken ct)
    {
        var keys = Builders<Notification>.IndexKeys;
        return _collection.Indexes.CreateManyAsync(
        [
            new(keys.Ascending(item => item.UserId).Descending(item => item.CreatedAtUtc).Descending(item => item.Id),
                new() { Name = "notifications_history" }),
            new(keys.Ascending(item => item.UserId).Ascending(item => item.ReadAtUtc),
                new() { Name = "notifications_unread" })
        ], ct);
    }

    public async Task<Notification> StoreAsync(Notification notification, CancellationToken ct)
    {
        var id = Filters.Eq(item => item.Id, notification.Id);
        var onInsert = Builders<Notification>.Update
            .SetOnInsert(item => item.UserId, notification.UserId)
            .SetOnInsert(item => item.Kind, notification.Kind)
            .SetOnInsert(item => item.LocalDate, notification.LocalDate)
            .SetOnInsert(item => item.CreatedAtUtc, notification.CreatedAtUtc)
            .SetOnInsert(item => item.ReadAtUtc, null)
            .SetOnInsert(item => item.HabitId, notification.HabitId)
            .SetOnInsert(item => item.HabitName, notification.HabitName)
            .SetOnInsert(item => item.Planned, notification.Planned)
            .SetOnInsert(item => item.Completed, notification.Completed)
            .SetOnInsert(item => item.Pending, notification.Pending);
        try
        {
            await _collection.UpdateOneAsync(id, onInsert, new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent insert won. Read it and apply the same compatibility check.
        }

        var saved = await _collection.Find(id).SingleAsync(ct);
        if (saved.UserId != notification.UserId || saved.Kind != notification.Kind)
            throw new NotificationContractException("Notification ID conflicted with its stored owner or kind.");

        return saved;
    }

    public async Task<NotificationPage> HistoryAsync(string userId, int limit, NotificationCursor? cursor, CancellationToken ct)
    {
        var owner = Filters.Eq(item => item.UserId, userId);
        var filter = owner;
        if (cursor is { } after)
        {
            filter &= Filters.Lt(item => item.CreatedAtUtc, after.CreatedAtUtc) |
                (Filters.Eq(item => item.CreatedAtUtc, after.CreatedAtUtc) & Filters.Lt(item => item.Id, after.Id));
        }

        var rows = await _collection.Find(filter).Sort(ByNewest).Limit(limit + 1).ToListAsync(ct);
        var next = rows.Count > limit
            ? new NotificationCursor(rows[limit - 1].CreatedAtUtc, rows[limit - 1].Id).Encode()
            : null;
        var unread = await _collection.CountDocumentsAsync(owner & Unread(), cancellationToken: ct);
        return new NotificationPage(rows.Take(limit).Select(NotificationDto.From).ToArray(), next, unread);
    }

    public async Task<bool> MarkReadAsync(string userId, string id, CancellationToken ct)
    {
        var owned = Filters.Eq(item => item.UserId, userId) & Filters.Eq(item => item.Id, id);
        var update = Builders<Notification>.Update.Set(item => item.ReadAtUtc, clock.GetUtcNow().UtcDateTime);
        var result = await _collection.UpdateOneAsync(owned & Unread(), update, cancellationToken: ct);
        return result.MatchedCount > 0 || await _collection.Find(owned).AnyAsync(ct);
    }

    private static FilterDefinition<Notification> Unread() => Filters.Eq(item => item.ReadAtUtc, null);
}
