using HabitTracker.Contracts;
using HabitTracker.Messaging;
using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using HabitTracker.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

[Collection("worker-infra")]
public sealed class OutboxFailureRollsBackCompletionTests(WorkerInfrastructureFixture fixture)
{
    private sealed class FailingOutbox(IMongoDatabase db) : MongoOutbox(db)
    {
        public override Task InsertAsync<T>(IClientSessionHandle session, T message, EventContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("outbox down");
    }

    [Fact]
    public async Task OutboxFailureLeavesJobRunning()
    {
        var jobs = fixture.Database.GetCollection<Job>("jobs");
        await jobs.DeleteManyAsync(FilterDefinition<Job>.Empty);

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.HabitReminder,
            UserId = "u1",
            HabitKey = "h1",
            LocalDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DueAtUtc = DateTime.UtcNow.AddMinutes(-1),
            TimeZoneId = "UTC",
            Status = JobStatus.Pending,
            LastUpdatedAtUtc = DateTime.UtcNow
        };
        await jobs.InsertOneAsync(job);

        var repo = new JobExecutorRepository(fixture.Database);
        var transactions = new MongoTransactions(fixture.MongoClient);
        var failingOutbox = new FailingOutbox(fixture.Database);

        var executor = new JobExecutorService(
            repo,
            failingOutbox,
            transactions,
            api: null!,
            readiness: null!,
            Options.Create(new WorkerOptions { LeaseSeconds = 120, LeaseRenewalSeconds = 30 }),
            TimeProvider.System,
            NullLogger<JobExecutorService>.Instance);

        // Захватываем job тем же owner'ом, что использует executor.
        var claimed = await repo.ClaimAsync(executor.Owner, DateTime.UtcNow, TimeSpan.FromMinutes(2), default);
        Assert.NotNull(claimed);

        var notification = new NotificationReadyV1(
            Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), claimed!.Id, claimed.UserId,
            claimed.Kind, claimed.LocalDate, DateTime.UtcNow,
            HabitId: claimed.HabitKey, HabitName: "test");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.CompleteAsync(claimed, notification, default));

        // Транзакция откатилась: completed не записан, outbox не записан, job остался Running.
        var stored = await jobs.Find(x => x.Id == claimed.Id).FirstAsync();
        Assert.Equal(JobStatus.Running, stored.Status);
        Assert.Equal(executor.Owner, stored.LeaseOwner);
    }
}