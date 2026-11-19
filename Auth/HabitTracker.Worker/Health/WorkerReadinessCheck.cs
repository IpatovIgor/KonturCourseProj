using HabitTracker.Worker.Services;
using MassTransit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace HabitTracker.Worker.Health;

public sealed class WorkerReadinessCheck(BootstrapReadiness readiness, IMongoDatabase database, IBusControl bus) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!readiness.IsReady)
            return HealthCheckResult.Unhealthy("bootstrap not completed");
        if (bus.CheckHealth().Status != BusHealthStatus.Healthy)
            return HealthCheckResult.Unhealthy("message endpoint unavailable");

        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return HealthCheckResult.Healthy();
        }
        catch (MongoException)
        {
            return HealthCheckResult.Unhealthy("database unavailable");
        }
    }
}
