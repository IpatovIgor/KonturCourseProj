using HabitTracker.Messaging;
using HabitTracker.Worker.Clients;
using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Consumers;
using HabitTracker.Worker.Contracts;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Health;
using HabitTracker.Worker.Scheduling;
using HabitTracker.Worker.Services;
using MassTransit;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<WorkerOptions>()
    .BindConfiguration(WorkerOptions.SectionName)
    .Configure<IConfiguration>((options, config) =>
    {
        options.BaseUrl = config["WorkerApi:BaseUrl"] ?? "";
        options.SharedKey = config["WorkerApi:SharedKey"] ?? "";
        options.DailySummaryEnabled = config.GetValue("Features:DailySummaryEnabled", true);
    })
    .Validate(o => { o.Validate(); return true; })
    .ValidateOnStart();

var mongoConnection = builder.Configuration["MongoDbSettings:ConnectionString"];
var mongoDatabase = builder.Configuration["MongoDbSettings:DatabaseName"];
if (string.IsNullOrWhiteSpace(mongoConnection) || string.IsNullOrWhiteSpace(mongoDatabase))
    throw new InvalidOperationException("Configure MongoDbSettings__ConnectionString and MongoDbSettings__DatabaseName.");

MongoClientSettings mongoSettings;
try
{
    mongoSettings = MongoClientSettings.FromConnectionString(mongoConnection);
    _ = new DatabaseNamespace(mongoDatabase);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    throw new InvalidOperationException("Invalid MongoDbSettings configuration.");
}

var rabbit = builder.Configuration.GetSection("RabbitMq").Get<RabbitMqSettings>() ?? new();
if (!Uri.TryCreate(rabbit.Host.Contains("://", StringComparison.Ordinal) ? rabbit.Host : $"rabbitmq://{rabbit.Host}",
        UriKind.Absolute, out var rabbitAddress) || rabbitAddress.Scheme is not ("rabbitmq" or "amqp" or "amqps") ||
    string.IsNullOrEmpty(rabbitAddress.Host) || !string.IsNullOrEmpty(rabbitAddress.UserInfo) ||
    string.IsNullOrWhiteSpace(rabbit.VirtualHost) || string.IsNullOrWhiteSpace(rabbit.Username) ||
    string.IsNullOrWhiteSpace(rabbit.Password))
    throw new InvalidOperationException("Invalid RabbitMq configuration. Set host, vhost and credentials.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoSettings));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDatabase));
builder.Services.AddSingleton<MongoOutbox>();
builder.Services.AddSingleton<MongoTransactions>();
builder.Services.AddSingleton<ScheduleCalculator>();
builder.Services.AddSingleton<ScheduleProjector>();
builder.Services.AddSingleton<ScheduleInbox>();
builder.Services.AddSingleton<MongoIndexInitializer>();
builder.Services.AddSingleton<BootstrapReadiness>();
builder.Services.AddSingleton<ScheduleRepository>();
builder.Services.AddSingleton<JobRepository>();
builder.Services.AddSingleton<JobExecutorRepository>();
builder.Services.AddHttpClient<WorkerApiClient>().ConfigurePrimaryHttpMessageHandler(() =>
    new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

builder.Services.AddHostedService<WorkerIndexStartup>();
builder.Services.AddMassTransit(reg =>
{
    reg.AddConsumer<HabitChangedConsumer>();
    reg.AddConsumer<SummarySettingsChangedConsumer>();
    reg.UsingRabbitMq((context, bus) =>
    {
        rabbit.Configure(bus);
        bus.ReceiveEndpoint(WorkerEndpoint.QueueName, endpoint => WorkerEndpoint.Configure(context, endpoint));
    });
});
builder.Services.AddHostedService<ScheduleBootstrapService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<JobExecutorService>();
builder.Services.AddHostedService<OutboxPublisher>();

builder.Services.AddHealthChecks()
    .AddCheck<WorkerReadinessCheck>("worker", tags: ["ready"], timeout: TimeSpan.FromSeconds(2));

var app = builder.Build();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
app.Run();
