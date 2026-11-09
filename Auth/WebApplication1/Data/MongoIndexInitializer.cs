namespace BackEnd.Data;

public sealed class MongoIndexInitializer(UserRepository users, HabitRepository habits) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await users.EnsureIndexesAsync(cancellationToken);
        await habits.EnsureIndexesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
