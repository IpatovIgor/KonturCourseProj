using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using HabitTracker.Contracts;
using HabitTracker.Messaging;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace BackEnd.Tests.Controllers;

public sealed partial class HabitsHttpTests
{
    [RabbitFact]
    public async Task V1EventsRouteOnlyToTheirEndpointWithWireMetadata()
    {
        var broker = Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_URI")!;
        var address = new Uri(broker);
        var vhost = $"transport-test-{Guid.NewGuid():N}";
        var user = Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_USER")!;
        var password = Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_PASSWORD")!;
        var rabbit = new RabbitMqSettings { Host = broker, VirtualHost = vhost, Username = user, Password = password };
        var bus = Bus.Factory.CreateUsingRabbitMq(rabbit.Configure);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        using var management = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_MANAGEMENT_URI")!)
        };
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        var now = DateTime.UtcNow;
        var correlationId = Guid.NewGuid();
        var context = new EventContext(correlationId, "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", "test=transport");
        var userId = ObjectId.GenerateNewId().ToString();
        var habitId = ObjectId.GenerateNewId().ToString();
        var habit = new HabitChangedV1(Guid.NewGuid(), now, correlationId, userId, habitId, (long)int.MaxValue + 1,
            "created", "active", "2026-01-01", [new("2026-01-01", [1])], "UTC", false, "20:00");
        var summary = new SummarySettingsChangedV1(Guid.NewGuid(), now, correlationId, userId, 1, true, "21:00", "UTC");
        var notification = new NotificationReadyV1(Guid.NewGuid(), now, correlationId, Guid.NewGuid(),
            userId, "dailySummary", "2026-01-01", now, Planned: 0, Completed: 0, Pending: 0);

        try
        {
            var definitions = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "rabbitmq-definitions.json"), ct))!.AsObject();
            definitions["vhosts"]![0]!["name"] = vhost;
            foreach (var collection in new[] { "exchanges", "queues", "bindings" })
                foreach (var entry in definitions[collection]!.AsArray())
                    entry!["vhost"] = vhost;
            definitions["permissions"] = new JsonArray(new JsonObject
            {
                ["user"] = user, ["vhost"] = vhost, ["configure"] = ".*", ["write"] = ".*", ["read"] = ".*"
            });
            using var created = await management.PostAsJsonAsync("/api/definitions", definitions, ct);
            created.EnsureSuccessStatusCode();
            await bus.StartAsync(ct);
            await PublishAsync(habit);
            await PublishAsync(summary);
            await PublishAsync(notification);
            var factory = new ConnectionFactory
            {
                HostName = address.Host, Port = address.Port, VirtualHost = vhost, UserName = user, Password = password
            };
            await using var connection = await factory.CreateConnectionAsync(ct);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
            var worker = await ReceiveAsync("worker-habit-events-v1");
            var notifications = await ReceiveAsync("notifications-ready-v1");
            Assert.Equal(2, worker.Count);
            Assert.Single(notifications);
            AssertEnvelope(worker, habit);
            AssertEnvelope(worker, summary);
            AssertEnvelope(notifications, notification);
            Assert.Equal(habit.Revision, worker[habit.EventId]["message"]!["revision"]!.GetValue<long>());

            async Task<Dictionary<Guid, JsonObject>> ReceiveAsync(string queue)
            {
                var result = new Dictionary<Guid, JsonObject>();
                while (await channel.BasicGetAsync(queue, autoAck: true, cancellationToken: ct) is { } delivery)
                {
                    var envelope = JsonNode.Parse(delivery.Body.Span)!.AsObject();
                    if (envelope["correlationId"]?.GetValue<Guid>() == correlationId)
                        result.Add(envelope["messageId"]!.GetValue<Guid>(), envelope);
                }
                return result;
            }
        }
        finally
        {
            await bus.StopAsync();
            using var removed = await management.DeleteAsync($"/api/vhosts/{vhost}", CancellationToken.None);
            if (removed.StatusCode != HttpStatusCode.NotFound)
                removed.EnsureSuccessStatusCode();
        }

        Task PublishAsync<T>(T message) where T : class, IVersionedEvent =>
            bus.Publish(message, publish => context.Apply(publish, message.EventId), ct);

        void AssertEnvelope<T>(Dictionary<Guid, JsonObject> messages, T message) where T : IVersionedEvent
        {
            var envelope = messages[message.EventId];
            Assert.Equal(message.EventId, envelope["message"]!["eventId"]!.GetValue<Guid>());
            Assert.Equal(correlationId, envelope["message"]!["correlationId"]!.GetValue<Guid>());
            Assert.Contains($"urn:message:HabitTracker.Contracts:{typeof(T).Name}",
                envelope["messageType"]!.AsArray().Select(value => value!.GetValue<string>()));
            Assert.Equal(context.TraceParent, envelope["headers"]!["traceparent"]!.GetValue<string>());
            Assert.Equal(context.TraceState, envelope["headers"]!["tracestate"]!.GetValue<string>());
        }
    }

    [RabbitFact]
    public async Task RabbitOutbox_DeliversAfterBrokerRecoveryAndRepeatsWithOriginalIdentity()
    {
        const string queue = "worker-habit-events-v1";
        const string vhost = "habit-tracker";
        const string limit = $"/api/vhost-limits/{vhost}/max-connections";
        const string traceState = "test=value";
        var correlation = Guid.NewGuid().ToString();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        var user = Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_USER")!;
        var password = Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_PASSWORD")!;
        var broker = new Uri(Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_URI")!);
        using var management = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_MANAGEMENT_URI")!)
        };
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

        using var noConnections = new StringContent("""{"value":0}""", Encoding.UTF8, "application/json");
        using var blocked = await management.PutAsync(limit, noConnections, ct);
        blocked.EnsureSuccessStatusCode();
        using var app = new HabitsApplication(useRabbit: true);
        try
        {
            using var http = app.CreateClient();
            Assert.NotNull(app.Services.GetService<MassTransit.IBus>());
            await LoginAsync(http, "broker@example.com");
            http.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
            http.DefaultRequestHeaders.Add("traceparent", "00-1cafe12345abcd12345abcd12345abcd-1234567890abcdef-01");
            http.DefaultRequestHeaders.Add("tracestate", traceState);

            using var created = await http.PostAsJsonAsync("/api/habits",
                new { name = "Зарядка", daysOfWeek = new[] { 1, 3, 5 } }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var path = Assert.IsType<Uri>(created.Headers.Location).OriginalString;
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(path, ct)).StatusCode);
            using var saved = await http.PutAsJsonAsync("/api/profile/summary-settings",
                new { enabled = true, localTime = "07:00" }, ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var database = app.Services.GetRequiredService<IMongoDatabase>();
            var events = database.GetCollection<BsonDocument>("outboxEvents");
            var habitFilter = Builders<BsonDocument>.Filter.Eq("contract", "HabitChangedV1");
            var summaryFilter = Builders<BsonDocument>.Filter.Eq("contract", "SummarySettingsChangedV1");
            Assert.Equal(2, await events.CountDocumentsAsync(Builders<BsonDocument>.Filter.Ne("status", "published"), cancellationToken: ct));
            await WaitUntilAsync(async () => (await events.Find(habitFilter).SingleAsync(ct))["attempts"].ToInt32() > 0
                || (await events.Find(summaryFilter).SingleAsync(ct))["attempts"].ToInt32() > 0, ct);

            // Keep the MongoDB write after broker confirmation from succeeding for the habit event only.
            var rejectAcknowledgement = new BsonDocument("$nor", new BsonArray
            {
                new BsonDocument { { "contract", "HabitChangedV1" }, { "status", "published" } }
            });
            await SetValidatorAsync(rejectAcknowledgement);
            using var unblocked = await management.DeleteAsync(limit, ct);
            unblocked.EnsureSuccessStatusCode();

            // Start reading only after the summary has been confirmed. Both messages were routed without a consumer.
            await WaitUntilAsync(async () =>
                (await events.Find(summaryFilter).SingleAsync(ct))["status"] == "published", ct);
            var factory = new ConnectionFactory
            {
                HostName = broker.Host, Port = broker.Port, VirtualHost = vhost, UserName = user, Password = password
            };
            await using var connection = await factory.CreateConnectionAsync(ct);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
            JsonObject? habitEnvelope = null;
            JsonObject? summaryEnvelope = null;
            while (habitEnvelope is null || summaryEnvelope is null)
            {
                var envelope = await ReadEnvelopeAsync(correlation);
                if (envelope["message"]!["habitId"] is null)
                    summaryEnvelope = envelope;
                else
                    habitEnvelope = envelope;
            }

            var firstId = habitEnvelope["messageId"]!.GetValue<string>();
            Assert.Equal(firstId, habitEnvelope["message"]!["eventId"]!.GetValue<string>());
            Assert.Equal("created", habitEnvelope["message"]!["change"]!.GetValue<string>());
            Assert.Equal("07:00", summaryEnvelope["message"]!["localTime"]!.GetValue<string>());
            Assert.Equal(correlation, summaryEnvelope["correlationId"]!.GetValue<string>());
            Assert.Equal(correlation, habitEnvelope["correlationId"]!.GetValue<string>());
            Assert.Equal(correlation, habitEnvelope["message"]!["correlationId"]!.GetValue<string>());
            Assert.Equal(traceState, habitEnvelope["headers"]!["tracestate"]!.GetValue<string>());
            Assert.StartsWith("00-1cafe12345abcd12345abcd12345abcd-", habitEnvelope["headers"]!["traceparent"]!.GetValue<string>());

            var claimed = await events.Find(habitFilter).SingleAsync(ct);
            Assert.Equal("publishing", claimed["status"].AsString);
            rejectAcknowledgement["$nor"][0]["leaseVersion"] =
                new BsonDocument("$lte", claimed["leaseVersion"]);
            await SetValidatorAsync(rejectAcknowledgement);
            app.Clock.Now = claimed["leaseUntil"].ToUniversalTime().AddSeconds(1);
            var second = await ReadEnvelopeAsync(correlation);
            Assert.Equal(firstId, second["messageId"]!.GetValue<string>());
            Assert.Equal(firstId, second["message"]!["eventId"]!.GetValue<string>());
            await WaitUntilAsync(async () =>
                (await events.Find(habitFilter).SingleAsync(ct))["status"] == "published", ct);
            Assert.True((await events.Find(habitFilter).SingleAsync(ct))["leaseVersion"].ToInt64()
                > claimed["leaseVersion"].ToInt64());

            Task SetValidatorAsync(BsonDocument validator) => database.RunCommandAsync<BsonDocument>(
                new BsonDocument { { "collMod", "outboxEvents" }, { "validator", validator } }, cancellationToken: ct);

            async Task<JsonObject> ReadEnvelopeAsync(string expectedCorrelation)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var delivery = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken: ct);
                    if (delivery is not null)
                    {
                        var envelope = JsonNode.Parse(delivery.Body.Span)!.AsObject();
                        if (envelope["correlationId"]?.GetValue<string>() == expectedCorrelation)
                            return envelope;
                    }
                    else
                        await Task.Delay(100, ct);
                }
            }
        }
        finally
        {
            using var cleanup = await management.DeleteAsync(limit, CancellationToken.None);
            if (cleanup.StatusCode != HttpStatusCode.NotFound)
                cleanup.EnsureSuccessStatusCode();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> isReady, CancellationToken ct)
    {
        while (!await isReady())
            await Task.Delay(100, ct);
    }
}

public sealed class RabbitFactAttribute : FactAttribute
{
    public RabbitFactAttribute()
    {
        if (new[] { "KONTUR_TEST_MONGO_URI", "KONTUR_TEST_RABBIT_URI", "KONTUR_TEST_RABBIT_MANAGEMENT_URI",
            "KONTUR_TEST_RABBIT_USER", "KONTUR_TEST_RABBIT_PASSWORD" }
            .Any(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))))
            Skip = "Start the isolated WebAPI test services and set KONTUR_TEST_* variables.";
    }
}
