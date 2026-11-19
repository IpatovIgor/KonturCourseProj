using HabitTracker.Messaging;
using HabitTracker.Worker.Clients;
using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Consumers;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Health;
using HabitTracker.Worker.Scheduling;
using HabitTracker.Worker.Services;
using MassTransit;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// --- Options ---
builder.Services.AddOptions<WorkerOptions>()
    .BindConfiguration(WorkerOptions.SectionName)
    .Validate(o => { o.Validate(); return true; })
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);

// --- Mongo (своя БД Worker) ---
builder.Services.AddSingleton<IMongoClient>(_ =>
    new MongoClient(builder.Configuration["Mongo:ConnectionString"]));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>()
      .GetDatabase(builder.Configuration["Mongo:DatabaseName"] ?? "habit_tracker_worker"));
builder.Services.AddSingleton<MongoOutbox>();

// --- Worker services ---
builder.Services.AddSingleton<MongoTransactions>();
builder.Services.AddSingleton<ScheduleCalculator>();
builder.Services.AddSingleton<ScheduleProjector>();
builder.Services.AddSingleton<MongoIndexInitializer>();
builder.Services.AddSingleton<BootstrapReadiness>();

// --- Repositories ---
builder.Services.AddSingleton<ScheduleRepository>();
builder.Services.AddSingleton<JobRepository>();
builder.Services.AddSingleton<JobExecutorRepository>();

// --- Worker API client + bootstrap ---
builder.Services.AddHttpClient<WorkerApiClient>();
builder.Services.AddHostedService<ScheduleBootstrapService>();

// --- Scheduler + Executor ---
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<JobExecutorService>();

// --- MassTransit + RabbitMQ ---
var rabbit = builder.Configuration.GetSection("RabbitMq").Get<RabbitMqSettings>();
if (rabbit is not null && !string.IsNullOrWhiteSpace(rabbit.Host))
{
    builder.Services.AddMassTransit(reg =>
    {
        reg.AddConsumer<HabitChangedConsumer>();
        reg.AddConsumer<SummarySettingsChangedConsumer>();

        reg.UsingRabbitMq((context, bus) =>
        {
            rabbit.Configure(bus);
            bus.ReceiveEndpoint("worker-habit-events-v1", e =>
            {
                // Retry для временных сбоев (MongoDB, сеть). После 5 попыток — в _error.
                // В MassTransit 8.5.x параметр называется intervalDelta (не delta).
                e.UseMessageRetry(r => r.Exponential(
                    retryLimit: 5,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromSeconds(30),
                    intervalDelta: TimeSpan.FromSeconds(5)));

                e.ConfigureConsumer<HabitChangedConsumer>(context);
                e.ConfigureConsumer<SummarySettingsChangedConsumer>(context);
            });
        });
    });
    builder.Services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = false);
    builder.Services.AddHostedService<OutboxPublisher>();
}

// --- Health ---
builder.Services.AddHealthChecks()
    .AddCheck<WorkerReadinessCheck>("ready", tags: new[] { "ready" });

var app = builder.Build();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

app.Run();