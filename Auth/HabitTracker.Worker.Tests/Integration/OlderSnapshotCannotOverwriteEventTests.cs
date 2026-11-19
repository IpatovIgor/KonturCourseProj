using HabitTracker.Worker.Data;
using HabitTracker.Worker.Scheduling;
using MongoDB.Driver;
using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

[Collection("worker-infra")]
public sealed class OlderSnapshotCannotOverwriteEventTests(WorkerInfrastructureFixture fixture)
{
    [Fact]
    public async Task OlderSnapshotIsIgnored()
    {
        var habits = fixture.Database.GetCollection<HabitTracker.Worker.Models.HabitSchedule>("habitSchedules");
        await habits.DeleteManyAsync(FilterDefinition<HabitTracker.Worker.Models.HabitSchedule>.Empty);

        var projector = new ScheduleProjector(new ScheduleCalculator());
        var transactions = new MongoTransactions(fixture.MongoClient);

        var v8 = new HabitTracker.Worker.Contracts.HabitScheduleSnapshot("u1", "h1", 8, "active", "2026-01-01",
            new[] { new HabitTracker.Worker.Contracts.ScheduleVersionSnapshot("2026-01-01", new[] { 1, 2, 3, 4, 5, 6, 7 }) },
            "UTC", true, "20:00");
        await transactions.RunAsync(s => projector.ApplyHabitAsync(
            s, habits, v8, Guid.Empty, Guid.Empty, null, DateTimeOffset.UtcNow, default), default);

        var v7 = v8 with { Revision = 7 };
        var applied = await transactions.RunAsync(s => projector.ApplyHabitAsync(
            s, habits, v7, Guid.Empty, Guid.Empty, null, DateTimeOffset.UtcNow, default), default);

        Assert.False(applied);
        var stored = await habits.Find(x => x.Id == "h1").FirstAsync();
        Assert.Equal(8, stored.Revision);
    }
}