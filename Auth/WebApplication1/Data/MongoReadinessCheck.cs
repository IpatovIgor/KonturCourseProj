using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BackEnd.Data;

public sealed class MongoReadinessCheck(IMongoDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var response = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1),
            cancellationToken: cancellationToken);
        return response.Contains("setName") && response.GetValue("isWritablePrimary", false).AsBoolean
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("MongoDB replica set has no writable primary.");
    }
}
