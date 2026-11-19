using HabitTracker.Worker.Consumers;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Scheduling;
using HabitTracker.Messaging;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;
using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

public sealed class WorkerInfrastructureFixture : IAsyncLifetime
{
    private MongoDbContainer _mongo = null!;
    private RabbitMqContainer _rabbit = null!;
    private ServiceProvider _provider = null!;

    public IMongoClient MongoClient { get; private set; } = null!;
    public IMongoDatabase Database { get; private set; } = null!;
    public IBusControl Bus { get; private set; } = null!;
    public IServiceProvider Services => _provider;
    public event Action<CommandSucceededEvent>? MongoCommandSucceeded;

    public async Task InitializeAsync()
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _mongo = new MongoDbBuilder()
            .WithImage("mongo:7")
            .WithReplicaSet()
            .Build();
        await _mongo.StartAsync(startup.Token);

        _rabbit = new RabbitMqBuilder()
            .WithImage("rabbitmq:4.1-management")
            .WithUsername("worker-test")
            .WithPassword("worker-test-only")
            .Build();
        await _rabbit.StartAsync(startup.Token);

        // MongoDB.Driver 3.x требует directConnection для single-node replica set в контейнере:
        // без этого драйвер уходит в discovery и не может подключиться к реплике извне.
        var cs = _mongo.GetConnectionString();
        if (!cs.Contains("directConnection", StringComparison.OrdinalIgnoreCase))
            cs += (cs.Contains('?') ? "&" : "?") + "directConnection=true";

        var settings = MongoClientSettings.FromConnectionString(cs);
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandSucceededEvent>(e => MongoCommandSucceeded?.Invoke(e));
        MongoClient = new MongoClient(settings);
        Database = MongoClient.GetDatabase("habit_tracker_worker_test");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(MongoClient);
        services.AddSingleton(Database);
        services.AddSingleton<MongoOutbox>();
        services.AddSingleton<MongoTransactions>();
        services.AddSingleton<ScheduleCalculator>();
        services.AddSingleton<ScheduleProjector>();
        services.AddSingleton<ScheduleInbox>();
        services.AddSingleton<MongoIndexInitializer>();
        services.AddMassTransit(x =>
        {
            x.AddConsumer<HabitChangedConsumer>();
            x.AddConsumer<SummarySettingsChangedConsumer>();
            x.UsingRabbitMq((ctx, cfg) =>
            {
                var uri = new Uri(_rabbit.GetConnectionString());
                new RabbitMqSettings
                {
                    Host = $"rabbitmq://{uri.Host}:{uri.Port}",
                    VirtualHost = "/",
                    Username = "worker-test",
                    Password = "worker-test-only"
                }.Configure(cfg);
                cfg.ReceiveEndpoint(WorkerEndpoint.QueueName, e => WorkerEndpoint.Configure(ctx, e));
            });
        });
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<MongoIndexInitializer>().InitializeAsync(default);
        Bus = _provider.GetRequiredService<IBusControl>();
        await Bus.StartAsync(TimeSpan.FromSeconds(30));
    }

    public async Task DisposeAsync()
    {
        if (Bus is not null) await Bus.StopAsync(TimeSpan.FromSeconds(10));
        if (_provider is not null) await _provider.DisposeAsync();
        if (_rabbit is not null) await _rabbit.DisposeAsync();
        if (_mongo is not null) await _mongo.DisposeAsync();
    }
}