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
        await jobs.UpdateOneAsync(x => x.Id == job.Id, Builders<Job>.Update.Set(x => x.Status, JobStatus.Skipped)
            .Set(x => x.LastErrorCode, "ineligible"));

        // Tomorrow's changed weekdays produce a new revision but do not change today's deadline.
        await habits.UpdateOneAsync(x => x.Id == habit.Id,
            Builders<HabitSchedule>.Update.Set(x => x.NextDueAtUtc, job.DueAtUtc)
                .Set(x => x.NextLocalDate, job.LocalDate).Inc(x => x.Revision, 1));
        await a.TickAsync(default);
        var sameJob = await jobs.Find(FilterDefinition<Job>.Empty).SingleAsync();
        Assert.Equal(job.Id, sameJob.Id);
        Assert.Equal(JobStatus.Skipped, sameJob.Status);

        var movedDue = job.DueAtUtc.AddMinutes(15);
        // A retry delay is later than the newly selected time, but the original slot was earlier.
        await jobs.UpdateOneAsync(x => x.Id == job.Id, Builders<Job>.Update.Set(x => x.DueAtUtc, movedDue.AddMinutes(5)));
        await habits.UpdateOneAsync(x => x.Id == habit.Id,
            Builders<HabitSchedule>.Update.Set(x => x.NextDueAtUtc, movedDue)
                .Set(x => x.NextLocalDate, job.LocalDate).Inc(x => x.Revision, 1));
        await a.TickAsync(default);
        var rescheduled = await jobs.Find(FilterDefinition<Job>.Empty).SingleAsync();
        Assert.Equal(job.Id, rescheduled.Id);
        Assert.Equal(JobStatus.Pending, rescheduled.Status);
        Assert.Equal(3, rescheduled.ScheduleRevision);
        Assert.Equal(movedDue, rescheduled.DueAtUtc);

        await jobs.UpdateOneAsync(x => x.Id == job.Id, Builders<Job>.Update.Set(x => x.Status, JobStatus.Running));
        var newerDue = movedDue.AddMinutes(15);
        await habits.UpdateOneAsync(x => x.Id == habit.Id,
            Builders<HabitSchedule>.Update.Set(x => x.NextDueAtUtc, newerDue)
                .Set(x => x.NextLocalDate, job.LocalDate).Inc(x => x.Revision, 1));
        await habits.InsertOneAsync(new HabitSchedule
        {
            Id = "h2", UserId = "u1", Revision = 1, Status = "active", StartDate = habit.StartDate,
            ScheduleVersions = habit.ScheduleVersions, TimeZoneId = "UTC", ReminderEnabled = true,
            ReminderLocalTime = "20:30", NextDueAtUtc = newerDue, NextLocalDate = job.LocalDate
        });
        options.Value.BatchSize = 1;
        await a.TickAsync(default);
        Assert.Equal(job.LocalDate, (await habits.Find(x => x.Id == habit.Id).SingleAsync()).NextLocalDate);
        Assert.True(await jobs.Find(x => x.HabitKey == "h2" && x.LocalDate == job.LocalDate).AnyAsync());
        await jobs.UpdateOneAsync(x => x.Id == job.Id, Builders<Job>.Update.Set(x => x.Status, JobStatus.Skipped)
            .Set(x => x.LastErrorCode, "ineligible"));
        await a.TickAsync(default);
        Assert.Equal(JobStatus.Pending, (await jobs.Find(x => x.Id == job.Id).SingleAsync()).Status);

        var stored = await habits.Find(x => x.Id == "h1").FirstAsync();
        Assert.Equal("2026-11-20", stored.NextLocalDate);
    }
}
