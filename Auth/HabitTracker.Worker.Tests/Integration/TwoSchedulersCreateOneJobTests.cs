using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using HabitTracker.Worker.Scheduling;
using HabitTracker.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

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

        var due = DateTime.UtcNow.AddMinutes(-5);
        var habit = new HabitSchedule
        {
            Id = "h1", UserId = "u1", Revision = 1, Status = "active", StartDate = "2026-01-01",
            ScheduleVersions = new[] { new ScheduleVersion { EffectiveFrom = "2026-01-01", DaysOfWeek = new[] { 1, 2, 3, 4, 5, 6, 7 } } },
            TimeZoneId = "UTC", ReminderEnabled = true, ReminderLocalTime = "20:00",
            NextDueAtUtc = due, NextLocalDate = due.ToString("yyyy-MM-dd")
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
            TimeProvider.System,
            NullLogger<SchedulerService>.Instance);

        var a = NewScheduler();
        var b = NewScheduler();

        await Task.WhenAll(a.TickAsync(default), b.TickAsync(default));

        var count = await jobs.CountDocumentsAsync(FilterDefinition<Job>.Empty);
        Assert.Equal(1, count);

        var stored = await habits.Find(x => x.Id == "h1").FirstAsync();
        Assert.True(stored.NextDueAtUtc > due, "указатель должен сдвинуться");
    }
}