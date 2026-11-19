using HabitTracker.Contracts;
using HabitTracker.Messaging;
using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using HabitTracker.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace HabitTracker.Worker.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("worker-infra")]
public sealed class OutboxFailureRollsBackCompletionTests(WorkerInfrastructureFixture fixture)
{
    [Fact]
    public async Task FailedOutboxInsertRollsBackAndAnotherOwnerCanFinish()
    {
        var jobs = fixture.Database.GetCollection<Job>("jobs");
        await jobs.DeleteManyAsync(FilterDefinition<Job>.Empty);
        var clock = new TestClock(new DateTimeOffset(2026, 11, 19, 20, 0, 0, TimeSpan.Zero));
        var job = new Job
        {
            Id = Guid.NewGuid(), Kind = JobKind.HabitReminder, UserId = "u1", HabitKey = "h1",
            LocalDate = "2026-11-19", DueAtUtc = clock.Now.UtcDateTime, TimeZoneId = "UTC",
            Status = JobStatus.Pending, LastUpdatedAtUtc = clock.Now.UtcDateTime
        };
        await jobs.InsertOneAsync(job);

        var repo = new JobExecutorRepository(fixture.Database);
        var transactions = new MongoTransactions(fixture.MongoClient);
        var outbox = new MongoOutbox(fixture.Database);
        JobExecutorService Executor() => new(repo, outbox, transactions, api: null!, schedules: null!,
            readiness: null!, Options.Create(new WorkerOptions()), clock, NullLogger<JobExecutorService>.Instance);
        var executor = Executor();
        var claimed = Assert.IsType<Job>(await repo.ClaimAsync(executor.Owner, clock.Now.UtcDateTime,
            TimeSpan.FromMinutes(2), default));
        var notification = new NotificationReadyV1(Guid.NewGuid(), clock.Now.UtcDateTime, Guid.NewGuid(),
            job.Id, job.UserId, job.Kind, job.LocalDate, clock.Now.UtcDateTime, HabitId: job.HabitKey, HabitName: "test");

        Task SetValidatorAsync(BsonDocument validator) => fixture.Database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "collMod", "outboxEvents" }, { "validator", validator } });

        await SetValidatorAsync(new BsonDocument("contract", new BsonDocument("$ne", nameof(NotificationReadyV1))));
        try
        {
            await Assert.ThrowsAsync<MongoWriteException>(() => executor.CompleteAsync(claimed, notification, default));
            var stored = await jobs.Find(x => x.Id == claimed.Id).FirstAsync();
            Assert.Equal(JobStatus.Running, stored.Status);
            Assert.Equal(executor.Owner, stored.LeaseOwner);
        }
        finally
        {
            await SetValidatorAsync(new BsonDocument());
        }

        clock.Now = clock.Now.AddMinutes(3);
        var next = Executor();
        var reclaimed = Assert.IsType<Job>(await repo.ClaimAsync(next.Owner, clock.Now.UtcDateTime, TimeSpan.FromMinutes(2), default));
        await Assert.ThrowsAsync<LeaseLostException>(() => executor.CompleteAsync(claimed, notification, default));
        await next.CompleteAsync(reclaimed, notification, default);
        var events = fixture.Database.GetCollection<OutboxEvent>("outboxEvents");
        Assert.Equal(job.Id, (await events.Find(x => x.Id == notification.EventId).SingleAsync()).Read<NotificationReadyV1>().NotificationId);
    }
}
