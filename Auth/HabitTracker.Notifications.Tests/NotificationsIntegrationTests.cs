using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HabitTracker.Contracts;
using HabitTracker.Messaging;
using HabitTracker.Notifications.Contracts;
using HabitTracker.Notifications.Hubs;
using HabitTracker.Notifications.Services;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace HabitTracker.Notifications.Tests;

[Trait("Category", "Integration")]
public sealed class NotificationsIntegrationTests
{
    private const string Owner = "507f1f77bcf86cd799439011";
    private const string Other = "507f1f77bcf86cd799439012";
    private const string Key = "notifications-integration-only-secret-1234567890-xyz";
    private const string Issuer = "notifications-test";
    private const string Audience = "notifications-client";
    private const string Queue = "notifications-ready-v1";
    private static readonly DateTime Created = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HistoryPaginatesByTimeAndIdAndReadIsOwnerScopedAndIdempotent()
    {
        await using var server = await StartAsync();
        var ids = Enumerable.Range(1, 25).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:x12}")).ToArray();
        foreach (var id in ids)
            await server.PublishAsync(Ready(id));
        await server.PublishAsync(Ready(Guid.NewGuid(), Other));
        using var owner = server.Client(Token(Owner));
        using var other = server.Client(Token(Other));
        await WaitUntilAsync(async () => (await History(owner)).UnreadCount == 25);

        var first = await History(owner);
        Assert.Equal(ids.Reverse().Take(20).Select(id => id.ToString()), first.Items.Select(item => item.Id));
        Assert.NotNull(first.NextCursor);
        var second = await History(owner, first.NextCursor);
        Assert.Equal(ids.Reverse().Skip(20).Select(id => id.ToString()), second.Items.Select(item => item.Id));
        Assert.Null(second.NextCursor);
        Assert.Equal(25, second.UnreadCount);
        Assert.All((await History(other, first.NextCursor)).Items, item => Assert.DoesNotContain(item.Id, ids.Select(id => id.ToString())));

        var read = $"/api/notifications/{ids[^1]}/read";
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsync(read, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(read, null)).StatusCode);
        var marked = (await History(owner)).Items[0].ReadAtUtc;
        Assert.NotNull(marked);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(read, null)).StatusCode);
        await server.PublishAsync(Ready(ids[^1]) with { Planned = 8, Pending = 8 });
        await server.PublishAsync(Ready(Guid.NewGuid()));
        await WaitUntilAsync(async () => (await History(owner)).UnreadCount == 25);
        var saved = (await History(owner)).Items.Single(item => item.Id == ids[^1].ToString());
        Assert.Equal(marked, saved.ReadAtUtc);
        Assert.Equal(2, saved.Planned);
        foreach (var url in new[] { "?limit=0", "?limit=101", "?limit=no", "?cursor=", "?cursor=bad" })
        {
            using var response = await owner.GetAsync("/api/notifications" + url);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation_error", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync("/api/notifications/no/read", null)).StatusCode);
    }

    [Fact]
    public async Task JwtProtectsRestAndWebSocketAndHubRoutesToAllConnectionsOfTheOwner()
    {
        await using var server = await StartAsync();
        using var anonymous = server.Client();
        foreach (var rejected in new[]
        {
            (string?)null, Token(Owner, TimeSpan.FromSeconds(-1)), Token("non-objectid"),
            Token(Owner, issuer: "other-issuer"), Token(Owner, audience: "other-audience"),
            Token(Owner, key: Key + "-different"), Token(Owner, algorithm: SecurityAlgorithms.HmacSha384)
        })
        {
            using var invalid = server.Client(rejected);
            Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.GetAsync("/api/notifications")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.PostAsync("/hubs/notifications/negotiate", null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications?access_token=" + Token(Owner))).StatusCode);

        await using var noJwt = server.Hub(null);
        await using var expired = server.Hub(Token(Owner, TimeSpan.FromSeconds(-1)));
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => noJwt.StartAsync());
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => expired.StartAsync());

        await using var owner = server.Hub(Token(Owner.ToUpperInvariant(), TimeSpan.FromSeconds(10)));
        await using var second = server.Hub(Token(Owner));
        await using var other = server.Hub(Token(Other));
        var received = Listen(owner);
        var alsoReceived = Listen(second);
        var notReceived = Listen(other);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await Task.WhenAll(owner.StartAsync(), second.StartAsync(), other.StartAsync());
        var message = Ready(Guid.NewGuid());
        await server.PublishAsync(message);
        Assert.Equal(message.NotificationId.ToString(), (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Id);
        Assert.Equal(message.NotificationId.ToString(), (await alsoReceived.Task.WaitAsync(TimeSpan.FromSeconds(10))).Id);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(notReceived.Task.IsCompleted);
        using var otherHttp = server.Client(Token(Other));
        Assert.Empty((await History(otherHttp)).Items);
    }

    [Fact]
    public async Task QueueKeepsOfflineMessagesRetriesHubFailureAndReplaysFromErrorQueue()
    {
        var storedBeforeDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transient = Ready(Guid.NewGuid());
        var persistent = Ready(Guid.NewGuid());
        var attempts = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(85));
        var ct = deadline.Token;
        var settings = RabbitSettings();
        var bus = Bus.Factory.CreateUsingRabbitMq(settings.Configure);
        await bus.StartAsync(ct);
        try
        {
            var offline = Ready(Guid.NewGuid());
            await PublishAsync(bus, offline, ct);
            await using var server = await StartAsync(services => services.AddSingleton<INotificationDelivery>(sp =>
                new TestDelivery(async (saved, token) =>
                {
                    var mongo = sp.GetRequiredService<IMongoDatabase>().GetCollection<MongoDB.Bson.BsonDocument>("notifications");
                    var persisted = await mongo.Find(new MongoDB.Bson.BsonDocument("_id", saved.Id)).SingleAsync(token);
                    Assert.Equal(saved.UserId, persisted["userId"].AsString);
                    storedBeforeDelivery.TrySetResult();
                    if (saved.Id == transient.NotificationId.ToString() && Interlocked.Increment(ref attempts) == 1)
                        throw new IOException("Simulated transient send failure.");
                    if (saved.Id == persistent.NotificationId.ToString())
                        throw new IOException("Simulated persistent send failure.");
                })));
            using var http = server.Client(Token(Owner));
            await storedBeforeDelivery.Task.WaitAsync(ct);
            await WaitUntilAsync(async () => (await History(http)).Items.Any(item => item.Id == offline.NotificationId.ToString()), ct);

            await PublishAsync(bus, transient, ct);
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref attempts) == 2), ct);
            Assert.Single((await History(http)).Items.Where(item => item.Id == transient.NotificationId.ToString()));

            await using var rabbit = await OpenRabbitAsync(settings, ct);
            await using var channel = await rabbit.CreateChannelAsync(cancellationToken: ct);
            var immediate = Stopwatch.StartNew();
            var invalid = Ready(Guid.NewGuid()) with { Kind = "invalid" };
            await PublishAsync(bus, invalid, ct);
            await ReadEnvelopeAsync(channel, Queue + "_error", invalid.EventId, ct);
            var conflict = transient with { EventId = Guid.NewGuid(), UserId = Other };
            await PublishAsync(bus, conflict, ct);
            await ReadEnvelopeAsync(channel, Queue + "_error", conflict.EventId, ct);
            Assert.True(immediate.Elapsed < TimeSpan.FromSeconds(12));

            var endpoint = await bus.GetSendEndpoint(new Uri("queue:" + Queue));
            var unknown = new UnknownMessage(Guid.NewGuid());
            await endpoint.Send(unknown, context => context.MessageId = unknown.Id, ct);
            await ReadEnvelopeAsync(channel, Queue + "_skipped", unknown.Id, ct);

            await PublishAsync(bus, persistent, ct);
            var failure = await ReadEnvelopeAsync(channel, Queue + "_error", persistent.EventId, ct);
            Assert.Equal(3, Assert.IsType<int>(failure.BasicProperties.Headers!["MT-Fault-RetryCount"]));
            Assert.Single((await History(http)).Items.Where(item => item.Id == persistent.NotificationId.ToString()));

            await server.StopAsync();
            await using var restored = await StartAsync(databaseName: server.DatabaseName);
            await using var live = restored.Hub(Token(Owner));
            var delivered = Listen(live);
            await live.StartAsync(ct);
            await channel.BasicPublishAsync(Queue, "", mandatory: true,
                new BasicProperties(failure.BasicProperties), failure.Body, ct);
            Assert.Equal(persistent.NotificationId.ToString(), (await delivered.Task.WaitAsync(ct)).Id);
            using var recovered = restored.Client(Token(Owner));
            Assert.Equal(3, (await History(recovered)).Items.Count);
        }
        finally
        {
            await bus.StopAsync();
        }
    }

    [Fact]
    public async Task UnavailableMongoKeepsProcessLiveAndExhaustsTheRetryPolicy()
    {
        const string unavailableMongo = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=150";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var server = await StartAsync(unavailableMongo: unavailableMongo);
        using var http = server.Client();
        // The index initializer encounters the same server-selection error as the consumer.
        await Task.Delay(500, ct);
        Assert.False(server.Stopping);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/health/ready", ct)).StatusCode);

        var message = Ready(Guid.NewGuid());
        await server.PublishAsync(message);
        await using var rabbit = await OpenRabbitAsync(RabbitSettings(), ct);
        await using var channel = await rabbit.CreateChannelAsync(cancellationToken: ct);
        var failure = await ReadEnvelopeAsync(channel, Queue + "_error", message.EventId, ct);
        Assert.Equal(3, Assert.IsType<int>(failure.BasicProperties.Headers!["MT-Fault-RetryCount"]));
    }

    private static TaskCompletionSource<NotificationDto> Listen(HubConnection hub)
    {
        var received = new TaskCompletionSource<NotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<NotificationDto>(NotificationsHub.ReceivedEvent, item => received.TrySetResult(item));
        return received;
    }

    private static async Task<NotificationPage> History(HttpClient client, string? cursor = null) =>
        (await client.GetFromJsonAsync<NotificationPage>("/api/notifications" + (cursor is null ? "" : "?cursor=" + cursor)))!;

    private static NotificationReadyV1 Ready(Guid id, string user = Owner) =>
        new(Guid.NewGuid(), Created, Guid.NewGuid(), id, user, "dailySummary", "2026-11-15", Created,
            Planned: 2, Completed: 0, Pending: 2);

    private static Task PublishAsync(IPublishEndpoint bus, NotificationReadyV1 message, CancellationToken ct) =>
        bus.Publish(message, context => EventContext.Capture(message.CorrelationId).Apply(context, message.EventId), ct);

    private static string Token(string sub, TimeSpan? lifetime = null, string issuer = Issuer,
        string audience = Audience, string key = Key, string algorithm = SecurityAlgorithms.HmacSha256)
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience,
            [new Claim(JwtRegisteredClaimNames.Sub, sub)], now.AddMinutes(-1), now + (lifetime ?? TimeSpan.FromMinutes(2)),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm)));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
            await Task.Delay(100, deadline.Token);
    }

    private static string Required(string key) => Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Set {key} to run the Notifications integration tests.");

    private static RabbitMqSettings RabbitSettings() => new()
    {
        Host = Required("KONTUR_TEST_RABBIT_URI"),
        Username = Required("KONTUR_TEST_RABBIT_USER"),
        Password = Required("KONTUR_TEST_RABBIT_PASSWORD")
    };

    private static Task<IConnection> OpenRabbitAsync(RabbitMqSettings settings, CancellationToken ct)
    {
        var uri = new Uri(settings.Host);
        return new ConnectionFactory
        {
            HostName = uri.Host, Port = uri.Port, VirtualHost = settings.VirtualHost,
            UserName = settings.Username, Password = settings.Password
        }.CreateConnectionAsync(ct);
    }

    private static async Task<BasicGetResult> ReadEnvelopeAsync(IChannel channel, string queue, Guid id, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
                var delivery = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken: ct);
                if (delivery?.BasicProperties.MessageId == id.ToString())
                    return delivery;
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException error) when (error.ShutdownReason?.ReplyCode == 406)
            {
                throw new InvalidOperationException($"Unexpected queue configuration for {queue}.", error);
            }
            await Task.Delay(100, ct);
        }
    }

    private static async Task<TestServer> StartAsync(Action<IServiceCollection>? configure = null,
        string? databaseName = null, string? unavailableMongo = null)
    {
        var settings = RabbitSettings();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(NotificationsApplication).Assembly.GetName().Name,
            Args = ["--urls", "http://127.0.0.1:0"]
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MongoDbSettings:ConnectionString"] = unavailableMongo ?? Required("KONTUR_TEST_MONGO_URI"),
            ["MongoDbSettings:DatabaseName"] = databaseName ?? "notifications_tests_" + Guid.NewGuid().ToString("N"),
            ["RabbitMq:Host"] = settings.Host, ["RabbitMq:Username"] = settings.Username,
            ["RabbitMq:Password"] = settings.Password, ["RabbitMq:VirtualHost"] = settings.VirtualHost,
            ["JwtSettings:Secret"] = Key, ["JwtSettings:Issuer"] = Issuer, ["JwtSettings:Audience"] = Audience
        });
        configure?.Invoke(builder.Services);
        var app = NotificationsApplication.Build(builder);
        var server = new TestServer(app, dropDatabase: unavailableMongo is null);
        try
        {
            await app.StartAsync();
            using var http = server.Client();
            if (unavailableMongo is null)
                await WaitUntilAsync(async () => (await http.GetAsync("/health/ready")).IsSuccessStatusCode);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    private sealed class TestServer(WebApplication app, bool dropDatabase) : IAsyncDisposable
    {
        public bool Stopping => app.Lifetime.ApplicationStopping.IsCancellationRequested;
        public string DatabaseName => app.Services.GetRequiredService<IMongoDatabase>().DatabaseNamespace.DatabaseName;

        public HttpClient Client(string? token = null)
        {
            var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            if (token is not null)
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }

        public HubConnection Hub(string? token) => new HubConnectionBuilder()
            .WithUrl(app.Urls.Single() + "/hubs/notifications" + (token is null ? "" : "?access_token=" + token), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.Proxy = new WebProxy();
            }).Build();

        public Task PublishAsync(NotificationReadyV1 message) =>
            NotificationsIntegrationTests.PublishAsync(app.Services.GetRequiredService<IBus>(), message, CancellationToken.None);

        public Task StopAsync() => app.StopAsync();

        public async ValueTask DisposeAsync()
        {
            var database = app.Services.GetRequiredService<IMongoDatabase>();
            await app.StopAsync();
            if (dropDatabase)
                await database.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
            await app.DisposeAsync();
        }
    }

    private sealed class TestDelivery(Func<Models.Notification, CancellationToken, Task> send) : INotificationDelivery
    {
        public Task SendAsync(Models.Notification notification, CancellationToken ct) => send(notification, ct);
    }
}

public sealed record UnknownMessage(Guid Id);
