using HabitTracker.Notifications.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace HabitTracker.Notifications.Health;

public sealed class NotificationsReadinessCheck(IMongoDatabase database, MongoIndexInitializer indexes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        if (!indexes.Ready)
            return HealthCheckResult.Unhealthy("Notification indexes are unavailable.");

        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1),
                cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is MongoException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("Notification storage is unavailable.");
        }
    }
}
