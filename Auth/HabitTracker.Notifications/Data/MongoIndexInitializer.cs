using MongoDB.Driver;

namespace HabitTracker.Notifications.Data;

public sealed class MongoIndexInitializer(NotificationRepository repository, ILogger<MongoIndexInitializer> logger)
    : BackgroundService
{
    private volatile bool _ready;
    public bool Ready => _ready;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await repository.EnsureIndexesAsync(stoppingToken);
                _ready = true;
                return;
            }
            catch (Exception error) when (error is MongoException or TimeoutException)
            {
                logger.LogWarning("Creating notification indexes failed ({ErrorType})", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
