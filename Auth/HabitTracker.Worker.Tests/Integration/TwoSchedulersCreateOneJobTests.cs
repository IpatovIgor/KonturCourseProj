using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using HabitTracker.Worker.Scheduling;
using HabitTracker.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("worker-infra")]
public sealed class TwoSchedulersCreateOneJobTests(WorkerInfrastructureFixture fixture)
{
    [Fact]
    public async Task TwoConcurrentSchedulersCreateSingleJob()
    {
        var habits = fixture.Database.GetCollection<HabitSchedule>("habitSchedules");
        var jobs = fixture.Database.GetCollection<Job>("jobs");
        await habits.DeleteManyAsync(FilterDefinition<HabitSchedule>.Empty);
        await jobs.DeleteManyAsync(FilterDefinition<Job>.Empty);

        var clock = new TestClock(new DateTimeOffset(2026, 11, 19, 21, 0, 0, TimeSpan.Zero));
        var due = clock.Now.AddDays(-3).UtcDateTime;
        var correlation = Guid.NewGuid();
        var habit = new HabitSchedule
        {
            Id = "h1", UserId = "u1", Revision = 1, Status = "active", StartDate = "2026-01-01",
            ScheduleVersions = new[] { new ScheduleVersion { EffectiveFrom = "2026-01-01", DaysOfWeek = new[] { 1, 2, 3, 4, 5, 6, 7 } } },
            TimeZoneId = "UTC", ReminderEnabled = true, ReminderLocalTime = "20:00",
            NextDueAtUtc = due, NextLocalDate = due.ToString("yyyy-MM-dd"), LastCorrelationId = correlation
        };
        await habits.InsertOneAsync(habit);

        var options = Options.Create(new HabitTracker.Worker.Configuration.WorkerOptions { BatchSize = 10, PollIntervalSeconds = 1 });
        var transactions = new MongoTransactions(fixture.MongoClient);

        SchedulerService NewScheduler() => new(
            new ScheduleRepository(fixture.Database),
            new JobRepository(fixture.Database),
            new ScheduleCalculator(),
            transactions,
            readiness: null!,
            options,
            clock,
            NullLogger<SchedulerService>.Instance);

        var a = NewScheduler();
        var b = NewScheduler();

        Task SetValidatorAsync(BsonDocument validator) => fixture.Database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "collMod", "habitSchedules" }, { "validator", validator } });

        await SetValidatorAsync(new BsonDocument("nextLocalDate", new BsonDocument("$ne", "2026-11-20")));
        try
        {
            await Assert.ThrowsAsync<MongoWriteException>(() => a.TickAsync(default));
            Assert.False(await jobs.Find(FilterDefinition<Job>.Empty).AnyAsync());
            Assert.Equal(due, (await habits.Find(x => x.Id == habit.Id).SingleAsync()).NextDueAtUtc);
        }
        finally
        {
            await SetValidatorAsync(new BsonDocument());
        }

        await Task.WhenAll(a.TickAsync(default), b.TickAsync(default));

        var job = await jobs.Find(FilterDefinition<Job>.Empty).SingleAsync();
        Assert.Equal("2026-11-19", job.LocalDate);
        Assert.Equal(1, job.ScheduleRevision);
        Assert.Equal(correlation, job.LastCorrelationId);
        await jobs.UpdateOneAsync(x => x.Id == job.Id, Builders<Job>.Update.Set(x => x.Status, JobStatus.Skipped));

        // Simulate a newer projection of the same day after the user changes the schedule.
        await habits.UpdateOneAsync(x => x.Id == habit.Id,
            Builders<HabitSchedule>.Update.Set(x => x.NextDueAtUtc, job.DueAtUtc)
                .Set(x => x.NextLocalDate, job.LocalDate).Inc(x => x.Revision, 1));
        await a.TickAsync(default);
        var sameJob = await jobs.Find(FilterDefinition<Job>.Empty).SingleAsync();
        Assert.Equal(job.Id, sameJob.Id);
        Assert.Equal(JobStatus.Skipped, sameJob.Status);

        var stored = await habits.Find(x => x.Id == "h1").FirstAsync();
        Assert.Equal("2026-11-20", stored.NextLocalDate);
    }
}