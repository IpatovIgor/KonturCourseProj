using System.Net;
using System.Net.Http.Json;
using HabitTracker.Contracts;
using HabitTracker.Messaging;
using HabitTracker.Worker.Clients;
using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Contracts;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using HabitTracker.Worker.Services;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace HabitTracker.Worker.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("worker-infra")]
public sealed class ExecutionTests(WorkerInfrastructureFixture fixture)
{
    private static readonly DateTimeOffset Evening = new(2026, 11, 19, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledSummaryDoesNotExecuteAnExistingJob()
    {
        var clock = new TestClock(Evening);
        using var http = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(EligibleSummary())));
        var executor = MakeExecutor(http, clock, dailySummaryEnabled: false);
        var id = await InsertPendingAsync(clock);

        await executor.ExecuteOneAsync(await ClaimAsync(executor, clock), default);

        Assert.Equal(JobStatus.Skipped, (await GetAsync(id)).Status);
        Assert.False(await fixture.Database.GetCollection<OutboxEvent>("outboxEvents")
            .Find(FilterDefinition<OutboxEvent>.Empty).AnyAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TransientFailuresHaveBoundedRetries(HttpStatusCode status)
    {
        var clock = new TestClock(Evening);
        using var http = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var executor = MakeExecutor(http, clock);
        var id = await InsertPendingAsync(clock);
        var backoffMinutes = new[] { 1, 2, 4, 8 };
        for (var n = 0; n < 5; n++)
        {
            var job = await ClaimAsync(executor, clock);
            Assert.Equal(id, job.Id);
            await executor.ExecuteOneAsync(job, default);
            var stored = await GetAsync(id);
            Assert.Equal(n + 1, stored.HttpFailureCount);
            Assert.Equal(n + 1, stored.Attempts);
            Assert.Null(stored.LeaseOwner);
            if (n == 4)
            {
                Assert.Equal(JobStatus.Failed, stored.Status);
                Assert.Equal($"http_{(int)status}", stored.LastErrorCode);
            }
            else
            {
                Assert.Equal(JobStatus.Pending, stored.Status);
                Assert.Equal(clock.Now.AddMinutes(backoffMinutes[n]).UtcDateTime, stored.DueAtUtc);
                clock.Now = stored.DueAtUtc;
            }
        }
    }

    [Theory]
    [InlineData(404, JobStatus.Skipped, "not_found")]
    [InlineData(200, JobStatus.Skipped, "ineligible")]
    [InlineData(400, JobStatus.Failed, "http_400")]
    public async Task IneligibleAndPermanentErrorsDoNotPublish(int status, string expectedStatus, string reason)
    {
        var clock = new TestClock(Evening);
        using var http = new HttpClient(new TestHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = status == 200 ? JsonContent.Create(new ReminderEligibility(false, "Reading", 7)
                {
                    ScheduledDueAtUtc = Evening.AddMinutes(-30)
                }) : null
            })));
        var executor = MakeExecutor(http, clock);
        var id = await InsertPendingAsync(clock, JobKind.HabitReminder);
        await executor.ExecuteOneAsync(await ClaimAsync(executor, clock), default);
        var stored = await GetAsync(id);
        Assert.Equal(expectedStatus, stored.Status);
        Assert.Equal(reason, stored.LastErrorCode);
        Assert.Equal(status == 200 ? Evening.AddMinutes(-30).UtcDateTime : (DateTime?)null, stored.ScheduledDueAtUtc);
        Assert.Equal(0, stored.HttpFailureCount);
        Assert.False(await fixture.Database.GetCollection<OutboxEvent>("outboxEvents")
            .Find(FilterDefinition<OutboxEvent>.Empty).AnyAsync());
    }

    [Fact]
    public async Task BackoffCrossingMidnightSkips()
    {
        var clock = new TestClock(Evening.AddHours(3).AddMinutes(59));
        using var http = new HttpClient(new TestHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var executor = MakeExecutor(http, clock);
        var id = await InsertPendingAsync(clock, failures: 1);
        await executor.ExecuteOneAsync(await ClaimAsync(executor, clock), default);
        var stored = await GetAsync(id);
        Assert.Equal(JobStatus.Skipped, stored.Status);
        Assert.Equal("stale_local_date", stored.LastErrorCode);
        Assert.Equal(2, stored.HttpFailureCount);
    }

    [Fact]
    public async Task ReminderMovesLaterWithoutSpendingTheRetryBudget()
    {
        var clock = new TestClock(Evening);
        using var http = new HttpClient(new TestHttpHandler((request, _) =>
        {
            Assert.Equal("?date=2026-11-19", request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ReminderEligibility(false, "Reading", 7)
                {
                    Reason = "notDueYet", NextDueAtUtc = Evening.AddHours(1)
                })
            });
        }));
        var executor = MakeExecutor(http, clock);
        var id = await InsertPendingAsync(clock, JobKind.HabitReminder, failures: 2);
        await executor.ExecuteOneAsync(await ClaimAsync(executor, clock), default);
        var stored = await GetAsync(id);
        Assert.Equal(JobStatus.Pending, stored.Status);
        Assert.Equal(Evening.AddHours(1).UtcDateTime, stored.DueAtUtc);
        Assert.Equal(Evening.AddHours(1).UtcDateTime, stored.ScheduledDueAtUtc);
        Assert.Equal(2, stored.HttpFailureCount);
        Assert.Equal(7, stored.ScheduleRevision);
    }

    [Fact]
    public async Task MidnightWhileApiIsRunningStopsDelivery()
    {
        var clock = new TestClock(Evening.AddHours(4).AddSeconds(-1));
        using var http = new HttpClient(new TestHttpHandler((_, _) =>
        {
            clock.Now = clock.Now.AddSeconds(2);
            return Task.FromResult(EligibleSummary());
        }));
        var executor = MakeExecutor(http, clock);
        var id = await InsertPendingAsync(clock);
        await executor.ExecuteOneAsync(await ClaimAsync(executor, clock), default);
        Assert.Equal(JobStatus.Skipped, (await GetAsync(id)).Status);
        var outbox = fixture.Database.GetCollection<OutboxEvent>("outboxEvents");
        Assert.Equal(0, await outbox.CountDocumentsAsync(FilterDefinition<OutboxEvent>.Empty));
    }

    [Fact]
    public async Task RenewedLeaseIsExclusiveAndLossCancelsHttp()
    {
        var clock = new TestClock(Evening);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new HttpClient(new TestHttpHandler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        }));
        var executor = MakeExecutor(http, clock, renewalSeconds: 1);
        var id = await InsertPendingAsync(clock);
        var job = await ClaimAsync(executor, clock);
        var execution = executor.ExecuteOneAsync(job, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        clock.Now = Evening.AddSeconds(30);
        await ScheduleMessagesTests.EventuallyAsync(async () =>
            (await GetAsync(id)).LeaseUntil > job.LeaseUntil, timeout.Token);
        var repo = new JobExecutorRepository(fixture.Database);
        Assert.Null(await repo.ClaimAsync("other", job.LeaseUntil!.Value, TimeSpan.FromMinutes(2), timeout.Token));
        clock.Now = (await GetAsync(id)).LeaseUntil!.Value.AddSeconds(1);
        var winner = await repo.ClaimAsync("other", clock.Now.UtcDateTime, TimeSpan.FromMinutes(2), timeout.Token);
        Assert.NotNull(winner);
        await execution.WaitAsync(timeout.Token);
        var stored = await GetAsync(id);
        Assert.Equal("other", stored.LeaseOwner);
        Assert.Equal(0, stored.HttpFailureCount);
    }

    [Fact]
    public async Task EmptySummaryIsDeliveredAndReplayHasStableIdentity()
    {
        var clock = new TestClock(Evening);
        using var http = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(EligibleSummary())));
        var executor = MakeExecutor(http, clock);
        var id = await InsertPendingAsync(clock);
        var messages = System.Threading.Channels.Channel.CreateUnbounded<(NotificationReadyV1 Event, Guid? MessageId, string? TraceState)>();
        var endpoint = fixture.Bus.ConnectReceiveEndpoint("worker-tests-ready", configurator =>
        {
            configurator.Handler<NotificationReadyV1>(context =>
            {
                messages.Writer.TryWrite((context.Message, context.MessageId, context.Headers.Get<string>("tracestate")));
                return Task.CompletedTask;
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        try
        {
            await endpoint.Ready.WaitAsync(ct);
            await executor.ExecuteOneAsync(await ClaimAsync(executor, clock), ct);
            Assert.Equal(JobStatus.Completed, (await GetAsync(id)).Status);
            var outbox = new MongoOutbox(fixture.Database);
            using var publisher = new OutboxPublisher(outbox, fixture.Bus, clock, NullLogger<OutboxPublisher>.Instance);
            await publisher.StartAsync(ct);
            var first = await messages.Reader.ReadAsync(ct);
            await publisher.StopAsync(ct);
            Assert.Equal(id, first.Event.NotificationId);
            Assert.Equal(first.Event.EventId, first.MessageId);
            Assert.Equal((0, 0, 0), (first.Event.Planned, first.Event.Completed, first.Event.Pending));
            Assert.Equal("test=1", first.TraceState);

            // Simulate a publisher that exited after publish and before recording confirmation.
            var events = fixture.Database.GetCollection<OutboxEvent>("outboxEvents");
            await events.UpdateOneAsync(x => x.Id == first.Event.EventId,
                Builders<OutboxEvent>.Update.Set(x => x.Status, "publishing")
                    .Set(x => x.LeaseUntil, clock.Now.AddSeconds(-1).UtcDateTime), cancellationToken: ct);
            using var restarted = new OutboxPublisher(outbox, fixture.Bus, clock, NullLogger<OutboxPublisher>.Instance);
            await restarted.StartAsync(ct);
            var replay = await messages.Reader.ReadAsync(ct);
            Assert.Equal(first.Event.EventId, replay.Event.EventId);
            Assert.Equal(first.Event.NotificationId, replay.Event.NotificationId);
            await restarted.StopAsync(ct);
        }
        finally
        {
            await endpoint.StopAsync();
        }
    }

    private IMongoCollection<Job> Jobs => fixture.Database.GetCollection<Job>("jobs");

    private async Task<Guid> InsertPendingAsync(TestClock clock, string kind = JobKind.DailySummary, int failures = 0)
    {
        await Jobs.DeleteManyAsync(FilterDefinition<Job>.Empty);
        await fixture.Database.GetCollection<OutboxEvent>("outboxEvents").DeleteManyAsync(FilterDefinition<OutboxEvent>.Empty);
        var id = Guid.NewGuid();
        await Jobs.InsertOneAsync(new Job
        {
            Id = id, Kind = kind, UserId = "u1", HabitKey = kind == JobKind.HabitReminder ? "h1" : "",
            LocalDate = "2026-11-19", TimeZoneId = "UTC", DueAtUtc = clock.Now.UtcDateTime,
            LastUpdatedAtUtc = clock.Now.UtcDateTime, HttpFailureCount = failures, LastTraceState = "test=1"
        });
        return id;
    }

    private async Task<Job> ClaimAsync(JobExecutorService executor, TestClock clock)
    {
        var job = await new JobExecutorRepository(fixture.Database).ClaimAsync(executor.Owner,
            clock.Now.UtcDateTime, TimeSpan.FromMinutes(2), default);
        return Assert.IsType<Job>(job);
    }

    private Task<Job> GetAsync(Guid id) => Jobs.Find(x => x.Id == id).SingleAsync();

    private JobExecutorService MakeExecutor(HttpClient http, TestClock clock, int renewalSeconds = 30, bool dailySummaryEnabled = true)
    {
        var options = Options.Create(new WorkerOptions { LeaseRenewalSeconds = renewalSeconds, DailySummaryEnabled = dailySummaryEnabled });
        return new JobExecutorService(new JobExecutorRepository(fixture.Database), new MongoOutbox(fixture.Database),
            new MongoTransactions(fixture.MongoClient), new WorkerApiClient(http, options),
            new ScheduleRepository(fixture.Database), new BootstrapReadiness(), options, clock,
            NullLogger<JobExecutorService>.Instance);
    }

    private static HttpResponseMessage EligibleSummary() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new SummaryEligibility(true, 0, 0, 0, 4))
    };
}
