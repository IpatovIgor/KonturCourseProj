using BackEnd.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace BackEnd.Data;

public class UserRepository(IMongoDatabase database, IOptions<MongoDbSettings> settings) : IUserRepository
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

    public virtual Task CreateAsync(User user) => _usersCollection.InsertOneAsync(user);

    public virtual Task UpdateAsync(User user) => _usersCollection.UpdateOneAsync(
        item => item.Id == user.Id, Builders<User>.Update.Set(item => item.LastLoginAt, user.LastLoginAt));
}
