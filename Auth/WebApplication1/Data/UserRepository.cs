using BackEnd.Models;
using BackEnd.Services;
using HabitTracker.Messaging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace BackEnd.Data;

public class UserRepository(IMongoDatabase database, IOptions<MongoDbSettings> settings,
    MongoOutbox outbox, TimeProvider clock, RequestEventContext events,
    IOptions<NotificationFeatures> features) : IUserRepository
{
    private readonly IMongoCollection<User> _usersCollection =
        database.GetCollection<User>(settings.Value.UsersCollectionName);

    public Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var keys = Builders<User>.IndexKeys.Ascending(user => user.Email);
        var index = new CreateIndexModel<User>(keys, new CreateIndexOptions { Unique = true });
        return _usersCollection.Indexes.CreateOneAsync(index, cancellationToken: cancellationToken);
    }

    public virtual async Task<User?> GetByEmailAsync(string email) =>
        await _usersCollection.Find(user => user.Email == email).FirstOrDefaultAsync();

    public async Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken) =>
        await _usersCollection.Find(user => user.Id == id).FirstOrDefaultAsync(cancellationToken);

    public Task<User?> SaveSummaryAsync(string id, NotificationSettings settings, CancellationToken ct)
    {
        var context = events.Capture();
        return database.InSnapshotAsync<User?>(async (session, token) =>
        {
            var filter = Builders<User>.Filter.Eq(user => user.Id, id);
            var user = await _usersCollection.Find(session, filter).FirstOrDefaultAsync(token);
            if (user is null || (user.SummaryEnabled == settings.Enabled && user.SummaryLocalTime == settings.LocalTime))
                return user;

            var update = Builders<User>.Update.Set(item => item.SummaryEnabled, settings.Enabled)
                .Set(item => item.SummaryLocalTime, settings.LocalTime).Inc(item => item.SummaryRevision, 1);
            user = await _usersCollection.FindOneAndUpdateAsync(session, filter, update,
                new FindOneAndUpdateOptions<User> { ReturnDocument = ReturnDocument.After }, token);

            if (features.Value.NotificationsEnabled)
            {
                var message = SummaryScheduleSnapshot.From(user).AsEvent(clock.GetUtcNow().UtcDateTime, context);
                await outbox.InsertAsync(session, message, context, token);
            }
            return user;
        }, ct);
    }

    public virtual Task CreateAsync(User user) => _usersCollection.InsertOneAsync(user);

    public virtual Task UpdateAsync(User user) => _usersCollection.UpdateOneAsync(
        item => item.Id == user.Id, Builders<User>.Update.Set(item => item.LastLoginAt, user.LastLoginAt));
}
