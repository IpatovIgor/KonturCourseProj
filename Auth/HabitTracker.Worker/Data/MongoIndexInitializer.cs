using HabitTracker.Messaging;
using HabitTracker.Worker.Models;
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class MongoIndexInitializer(
    IMongoDatabase database,
    MongoOutbox outbox,
    ILogger<MongoIndexInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        var habits = database.GetCollection<HabitSchedule>("habitSchedules");
        await habits.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<HabitSchedule>(
                Builders<HabitSchedule>.IndexKeys.Ascending(x => x.NextDueAtUtc))
        }, ct);

        var summaries = database.GetCollection<SummarySchedule>("summarySchedules");
        await summaries.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<SummarySchedule>(
                Builders<SummarySchedule>.IndexKeys.Ascending(x => x.NextDueAtUtc))
        }, ct);

        var jobs = database.GetCollection<Job>("jobs");
        await jobs.Indexes.CreateManyAsync(new[]
        {
            // Уникальность: одно задание на (userId, kind, habitKey, localDate).
            // Без этого индекса две реплики Worker создадут два уведомления.
            new CreateIndexModel<Job>(
                Builders<Job>.IndexKeys
                    .Ascending(x => x.UserId)
                    .Ascending(x => x.Kind)
                    .Ascending(x => x.HabitKey)
                    .Ascending(x => x.LocalDate),
                new CreateIndexOptions { Unique = true, Name = "ux_jobs_logical" }),

            new CreateIndexModel<Job>(
                Builders<Job>.IndexKeys
                    .Ascending(x => x.Status)
                    .Ascending(x => x.DueAtUtc)
                    .Ascending(x => x.Id),
                new CreateIndexOptions { Name = "ix_jobs_due" }),

            new CreateIndexModel<Job>(
                Builders<Job>.IndexKeys
                    .Ascending(x => x.Status)
                    .Ascending(x => x.LeaseUntil)
                    .Ascending(x => x.Id),
                new CreateIndexOptions { Name = "ix_jobs_lease" })
        }, ct);

        await outbox.EnsureIndexesAsync(ct);
        logger.LogInformation("Worker Mongo indexes ensured.");
    }
}