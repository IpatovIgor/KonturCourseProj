using HabitTracker.Messaging;

namespace BackEnd.Data;

public sealed class MongoIndexInitializer(UserRepository users, HabitRepository habits, MongoOutbox outbox) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await users.EnsureIndexesAsync(cancellationToken);
        await habits.EnsureIndexesAsync(cancellationToken);
        await outbox.EnsureIndexesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
