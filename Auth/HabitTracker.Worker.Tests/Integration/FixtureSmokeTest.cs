using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

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
    public void RabbitMq_Is_Reachable()
    {
        Assert.NotNull(fixture.Bus);
        Assert.True(fixture.Bus.Address is not null);
    }
}