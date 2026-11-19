using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using MongoDB.Driver;
using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("worker-infra")]
public sealed class ExpiredOwnerCannotCompleteTests(WorkerInfrastructureFixture fixture)
{
    [Fact]
    public async Task ExpiredOwnerCannotFinishJob()
    {
        var jobs = fixture.Database.GetCollection<Job>("jobs");
        await jobs.DeleteManyAsync(FilterDefinition<Job>.Empty);

        var now = new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc);
        var job = new Job
        {
            Id = Guid.NewGuid(), Kind = JobKind.HabitReminder, UserId = "u1", HabitKey = "h1",
            LocalDate = "2026-11-18", DueAtUtc = now.AddMinutes(-1), TimeZoneId = "UTC",
            Status = JobStatus.Pending, LastUpdatedAtUtc = now
        };
        await jobs.InsertOneAsync(job);

        var repo = new JobExecutorRepository(fixture.Database);
        var a = await repo.ClaimAsync("owner-A", now, TimeSpan.FromSeconds(1), default);
        Assert.NotNull(a);

        now = now.AddSeconds(2);

        var b = await repo.ClaimAsync("owner-B", now, TimeSpan.FromMinutes(2), default);
        Assert.NotNull(b);
        Assert.Equal("owner-B", b!.LeaseOwner);

        var transactions = new MongoTransactions(fixture.MongoClient);
        var finishedByA = await transactions.RunAsync(session =>
            repo.FinishAsync(session, a!.Id, "owner-A", a.LeaseVersion, now,
                JobStatus.Completed, null, default), default);

        Assert.False(finishedByA);
        var stored = await jobs.Find(x => x.Id == job.Id).FirstAsync();
        Assert.Equal(JobStatus.Running, stored.Status);
        Assert.Equal("owner-B", stored.LeaseOwner);
    }
}