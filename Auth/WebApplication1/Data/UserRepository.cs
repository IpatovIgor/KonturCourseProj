using BackEnd.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace BackEnd.Data;

public class UserRepository : IUserRepository
{
    private readonly IMongoCollection<User> _usersCollection;

    public UserRepository(IOptions<MongoDbSettings> mongoDbSettings)
    {
        var mongoClient = new MongoClient(mongoDbSettings.Value.ConnectionString);
        var mongoDatabase = mongoClient.GetDatabase(mongoDbSettings.Value.DatabaseName);
        _usersCollection = mongoDatabase.GetCollection<User>(mongoDbSettings.Value.UsersCollectionName);

        CreateIndexes();
    }

    private void CreateIndexes()
    {
        var indexKeysDefinition = Builders<User>.IndexKeys.Ascending(u => u.Email);
        var indexOptions = new CreateIndexOptions { Unique = true };
        var indexModel = new CreateIndexModel<User>(indexKeysDefinition, indexOptions);
        
        _usersCollection.Indexes.CreateOne(indexModel);
    }

    public virtual async Task<User?> GetByEmailAsync(string email)
    {
        return await _usersCollection.Find(u => u.Email == email).FirstOrDefaultAsync();
    }

    public virtual async Task CreateAsync(User user)
    {
        await _usersCollection.InsertOneAsync(user);
    }
    
    public virtual async Task UpdateAsync(User user)
    {
        await _usersCollection.ReplaceOneAsync(u => u.Id == user.Id, user);
    }
}