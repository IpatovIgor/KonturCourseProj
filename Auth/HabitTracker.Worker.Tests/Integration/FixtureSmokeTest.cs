using HabitTracker.Worker.Health;
using HabitTracker.Worker.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("worker-infra")]
public sealed class FixtureSmokeTest(WorkerInfrastructureFixture fixture)
{
    [Fact]
    public async Task Mongo_Is_ReplicaSet()
    {
        var hello = await fixture.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument("hello", 1));
        Assert.True(hello.Contains("setName"), "Mongo должен быть replica set");
    }

    [Fact]
    public async Task ReadinessRequiresBootstrapAndRunningConsumer()
    {
        var readiness = new BootstrapReadiness();
        var check = new WorkerReadinessCheck(readiness, fixture.Database, fixture.Bus);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new(), ct)).Status);
        readiness.MarkReady();
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new(), ct)).Status);

        await fixture.Bus.StopAsync(ct);
        try
        {
            Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new(), ct)).Status);
        }
        finally
        {
            await fixture.Bus.StartAsync(ct);
        }
    }

    [Fact]
    public void RabbitMq_Is_Reachable()
    {
        Assert.NotNull(fixture.Bus);
        Assert.True(fixture.Bus.Address is not null);
    }
}