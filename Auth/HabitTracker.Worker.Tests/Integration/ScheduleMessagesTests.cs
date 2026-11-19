using System.Net;
using System.Net.Http.Json;
using HabitTracker.Contracts;
using HabitTracker.Messaging;
using HabitTracker.Worker.Clients;
using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Consumers;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using HabitTracker.Worker.Services;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using HabitTracker.Worker.Contracts;

namespace HabitTracker.Worker.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("worker-infra")]
public sealed class ScheduleMessagesTests(WorkerInfrastructureFixture fixture)
{
    private static HabitChangedV1 Habit(long revision) => new(
        Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(), revision, "edited", "active", "2026-01-01",
        [new("2026-01-01", [1, 2, 3, 4, 5, 6, 7])], "UTC", true, "20:00");

    [Fact]
    public async Task DuplicateAndOldEventsDoNotOverwriteArchive()
    {
        var archived = Habit(9) with { Status = "archived", Change = "archived" };
        var old = archived with { EventId = Guid.NewGuid(), Revision = 8, Status = "active", Change = "edited" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await PublishAsync(archived, ct);
        await PublishAsync(archived, ct);
        await PublishAsync(old, ct);
        var inbox = fixture.Database.GetCollection<InboxEvent>("inboxEvents");
        await EventuallyAsync(async () => await inbox.CountDocumentsAsync(
            x => x.Id == archived.EventId || x.Id == old.EventId, cancellationToken: ct) == 2, ct);
        var stored = await fixture.Database.GetCollection<HabitSchedule>("habitSchedules")
            .Find(x => x.Id == archived.HabitId).SingleAsync(ct);
        Assert.Equal(9, stored.Revision);
        Assert.Equal("archived", stored.Status);
        Assert.Null(stored.NextDueAtUtc);
        Assert.Equal(archived.CorrelationId, stored.LastCorrelationId);
    }

    [Fact]
    public async Task BootstrapRetriesAndOlderPageCannotOverwriteBrokersEvent()
    {
        var newer = Habit(8);
        var inbox = fixture.Database.GetCollection<InboxEvent>("inboxEvents");
        var markers = fixture.Database.GetCollection<BootstrapState>("bootstrapState");
        await markers.DeleteManyAsync(FilterDefinition<BootstrapState>.Empty);
        var readiness = new BootstrapReadiness();
        var rootPages = 0;
        var summaryPages = 0;
        var expectedCursor = ObjectId.GenerateNewId().ToString();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        using var http = new HttpClient(new TestHttpHandler(async (request, token) =>
        {
            Assert.False(readiness.IsReady);
            Assert.False(await markers.Find(FilterDefinition<BootstrapState>.Empty).AnyAsync(token));
            if (request.RequestUri!.AbsolutePath.EndsWith("summary-schedules", StringComparison.Ordinal))
            {
                if (++summaryPages == 1)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return Json(new SchedulePage<SummaryScheduleSnapshot>([], null));
            }
            if (!request.RequestUri.Query.Contains("&after=", StringComparison.Ordinal))
            {
                rootPages++;
                return Json(new SchedulePage<HabitScheduleSnapshot>([], expectedCursor));
            }
            Assert.Contains("&after=" + expectedCursor, request.RequestUri.Query);
            await PublishAsync(newer, token);
            await EventuallyAsync(() => inbox.Find(x => x.Id == newer.EventId).AnyAsync(token), token);
            return Json(new SchedulePage<HabitScheduleSnapshot>(
                [new(newer.UserId, newer.HabitId, 7, "archived", newer.StartDate,
                    [new(newer.StartDate, [1])], "UTC", false, "20:00")], null));
        }));
        var options = Options.Create(new WorkerOptions { BootstrapRetrySeconds = 1, HttpTimeoutSeconds = 7 });
        var api = new WorkerApiClient(http, options);
        using var bootstrap = new ScheduleBootstrapService(api, fixture.Database,
            fixture.Services.GetRequiredService<MongoTransactions>(), fixture.Services.GetRequiredService<ScheduleProjector>(),
            readiness, options, TimeProvider.System, NullLogger<ScheduleBootstrapService>.Instance);
        await bootstrap.StartAsync(ct);
        try
        {
            await readiness.WaitReadyAsync(ct);
            Assert.Equal(2, rootPages);
            Assert.Equal(2, summaryPages);
            var stored = await fixture.Database.GetCollection<HabitSchedule>("habitSchedules")
                .Find(x => x.Id == newer.HabitId).SingleAsync(ct);
            Assert.Equal(8, stored.Revision);
            Assert.Equal("active", stored.Status);
            Assert.True(await markers.Find(FilterDefinition<BootstrapState>.Empty).AnyAsync(ct));
        }
        finally
        {
            await bootstrap.StopAsync(default);
        }
    }

    [Fact]
    public async Task ForcedBootstrapDoesNotUseOldMarkerAndRejectsWrongKey()
    {
        var markers = fixture.Database.GetCollection<BootstrapState>("bootstrapState");
        await markers.ReplaceOneAsync(x => x.Id == BootstrapState.InitialSchedulesId,
            new BootstrapState { Id = BootstrapState.InitialSchedulesId, CompletedAtUtc = DateTime.UnixEpoch },
            new ReplaceOptions { IsUpsert = true });
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var http = new HttpClient(new TestHttpHandler(async (request, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                requestReceived.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            await retryAllowed.Task.WaitAsync(token);
            return request.RequestUri!.AbsolutePath.EndsWith("habit-schedules", StringComparison.Ordinal)
                ? Json(new SchedulePage<HabitScheduleSnapshot>([], null))
                : Json(new SchedulePage<SummaryScheduleSnapshot>([], null));
        }));
        var readiness = new BootstrapReadiness();
        var options = Options.Create(new WorkerOptions { BootstrapRetrySeconds = 1, ReloadSchedulesOnStartup = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        using var bootstrap = new ScheduleBootstrapService(new WorkerApiClient(http, options), fixture.Database,
            fixture.Services.GetRequiredService<MongoTransactions>(), fixture.Services.GetRequiredService<ScheduleProjector>(),
            readiness, options, TimeProvider.System, NullLogger<ScheduleBootstrapService>.Instance);
        await bootstrap.StartAsync(ct);
        try
        {
            await requestReceived.Task.WaitAsync(ct);
            Assert.False(readiness.IsReady);
            retryAllowed.TrySetResult();
            await readiness.WaitReadyAsync(ct);
            Assert.Equal(3, attempts);
            Assert.True((await markers.Find(x => x.Id == BootstrapState.InitialSchedulesId)
                .SingleAsync(ct)).CompletedAtUtc > DateTime.UnixEpoch);
        }
        finally
        {
            await bootstrap.StopAsync(default);
        }
    }

    [Fact]
    public async Task PoisonAndUnknownEnvelopesGoToSeparateQueues()
    {
        var poison = Habit(2) with { ReminderLocalTime = "no-time" };
        var fixedMessage = poison with { ReminderLocalTime = "20:00" };
        var bad = new TaskCompletionSource<HabitChangedV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        var skipped = new TaskCompletionSource<UnrelatedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var errorQueue = fixture.Bus.ConnectReceiveEndpoint(WorkerEndpoint.QueueName + "_error", endpoint =>
        {
            endpoint.ConfigureConsumeTopology = false;
            endpoint.Handler<HabitChangedV1>(context => { bad.TrySetResult(context.Message); return Task.CompletedTask; });
        });
        var skippedQueue = fixture.Bus.ConnectReceiveEndpoint(WorkerEndpoint.QueueName + "_skipped", endpoint =>
        {
            endpoint.ConfigureConsumeTopology = false;
            endpoint.Handler<UnrelatedMessage>(context => { skipped.TrySetResult(context.Message); return Task.CompletedTask; });
        });
        try
        {
            await Task.WhenAll(errorQueue.Ready, skippedQueue.Ready).WaitAsync(ct);
            await PublishAsync(poison, ct);
            var unsupported = new UnrelatedMessage(Guid.NewGuid());
            var endpoint = await fixture.Bus.GetSendEndpoint(new Uri("queue:" + WorkerEndpoint.QueueName));
            await endpoint.Send(unsupported, ct);
            Assert.Equal(poison.EventId, (await bad.Task.WaitAsync(ct)).EventId);
            Assert.Equal(unsupported.Id, (await skipped.Task.WaitAsync(ct)).Id);

            var inbox = fixture.Database.GetCollection<InboxEvent>("inboxEvents");
            Assert.False(await inbox.Find(x => x.Id == poison.EventId).AnyAsync(ct));
            await PublishAsync(fixedMessage, ct);
            await PublishAsync(fixedMessage, ct);
            await EventuallyAsync(() => inbox.Find(x => x.Id == poison.EventId).AnyAsync(ct), ct);
            Assert.Equal(1, await inbox.CountDocumentsAsync(x => x.Id == poison.EventId, cancellationToken: ct));
        }
        finally
        {
            await skippedQueue.StopAsync();
            await errorQueue.StopAsync();
        }
    }

    [Fact]
    public async Task ScheduleWriteFailureRollsBackInboxAndConsumerRetries()
    {
        var message = Habit(2);
        var inbox = fixture.Database.GetCollection<InboxEvent>("inboxEvents");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        var failedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCommand(CommandSucceededEvent command)
        {
            if (command.CommandName == "update" && command.Reply.TryGetValue("writeErrors", out var errors) &&
                errors.AsBsonArray.Any(error => error["code"] == 121))
                failedWrite.TrySetResult();
        }

        Task SetValidatorAsync(BsonDocument validator) => fixture.Database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "collMod", "habitSchedules" }, { "validator", validator } });

        await SetValidatorAsync(new BsonDocument("_id", new BsonDocument("$ne", message.HabitId)));
        fixture.MongoCommandSucceeded += OnCommand;
        try
        {
            await PublishAsync(message, ct);
            await failedWrite.Task.WaitAsync(ct);
            Assert.False(await inbox.Find(x => x.Id == message.EventId).AnyAsync(ct));
        }
        finally
        {
            fixture.MongoCommandSucceeded -= OnCommand;
            await SetValidatorAsync(new BsonDocument());
        }

        // The original envelope must be retried by the endpoint without a second Publish.
        await EventuallyAsync(() => inbox.Find(x => x.Id == message.EventId).AnyAsync(ct), ct);
        Assert.Equal(message.Revision, (await fixture.Database.GetCollection<HabitSchedule>("habitSchedules")
            .Find(x => x.Id == message.HabitId).SingleAsync(ct)).Revision);
    }

    private Task PublishAsync(HabitChangedV1 message, CancellationToken ct) =>
        fixture.Bus.Publish(message, publish =>
            new EventContext(message.CorrelationId, "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", "test=1")
                .Apply(publish, message.EventId), ct);

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    internal static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        while (!await condition())
            await Task.Delay(25, ct);
    }
}

public sealed record UnrelatedMessage(Guid Id);

internal sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
