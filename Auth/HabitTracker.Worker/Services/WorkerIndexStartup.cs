using HabitTracker.Worker.Data;

namespace HabitTracker.Worker.Services;

// Register before the bus and publisher so neither processes data ahead of the indexes.
public sealed class WorkerIndexStartup(MongoIndexInitializer indexes) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => indexes.InitializeAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
