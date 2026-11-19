using HabitTracker.Worker.Clients;
using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace HabitTracker.Worker.Services;

public sealed class ScheduleBootstrapService(
    WorkerApiClient api,
    IMongoDatabase database,
    MongoTransactions transactions,
    ScheduleProjector projector,
    MongoIndexInitializer indexInitializer,
    BootstrapReadiness readiness,
    IOptions<WorkerOptions> options,
    TimeProvider clock,
    ILogger<ScheduleBootstrapService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await indexInitializer.InitializeAsync(stoppingToken);

        var state = database.GetCollection<BootstrapState>("bootstrapState");
        var marker = await state.Find(Builders<BootstrapState>.Filter.Eq(x => x.Id, BootstrapState.InitialSchedulesId))
            .FirstOrDefaultAsync(stoppingToken);

        if (marker is not null && !options.Value.ReloadSchedulesOnStartup)
        {
            logger.LogInformation("Bootstrap marker present; skipping reload.");
            readiness.MarkReady();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Два обхода: второй ловит изменения, произошедшие между страницами первого.
                // События, пришедшие во время загрузки, применяются через projector по правилу
                // "версия строго больше" и не будут затёрты старым снимком.
                await RunOnceAsync(stoppingToken);
                await RunOnceAsync(stoppingToken);

                await state.ReplaceOneAsync(
                    Builders<BootstrapState>.Filter.Eq(x => x.Id, BootstrapState.InitialSchedulesId),
                    new BootstrapState
                    {
                        Id = BootstrapState.InitialSchedulesId,
                        CompletedAtUtc = clock.GetUtcNow().UtcDateTime
                    },
                    new ReplaceOptions { IsUpsert = true }, stoppingToken);

                logger.LogInformation("Bootstrap completed.");
                readiness.MarkReady();
                return;
            }
            catch (WorkerApiUnauthorizedException)
            {
                logger.LogError("Worker API rejected the key; readiness stays false. Check Worker__SharedKey.");
                await Task.Delay(TimeSpan.FromSeconds(options.Value.BootstrapRetrySeconds), clock, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Bootstrap attempt failed; retrying from first page.");
                await Task.Delay(TimeSpan.FromSeconds(options.Value.BootstrapRetrySeconds), clock, stoppingToken);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await LoadHabitsAsync(now, ct);
        await LoadSummariesAsync(now, ct);
    }

    private async Task LoadHabitsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var habits = database.GetCollection<HabitSchedule>("habitSchedules");
        string? cursor = null;
        do
        {
            var page = await api.HabitPageAsync(cursor, options.Value.BatchSize, ct);
            foreach (var snapshot in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                await transactions.RunAsync(async session =>
                {
                    await projector.ApplyHabitAsync(session, habits, snapshot,
                        Guid.Empty, Guid.Empty, null, now, ct);
                    return true;
                }, ct);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private async Task LoadSummariesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var summaries = database.GetCollection<SummarySchedule>("summarySchedules");
        string? cursor = null;
        do
        {
            var page = await api.SummaryPageAsync(cursor, options.Value.BatchSize, ct);
            foreach (var snapshot in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                await transactions.RunAsync(async session =>
                {
                    await projector.ApplySummaryAsync(session, summaries, snapshot,
                        Guid.Empty, Guid.Empty, null, now, ct);
                    return true;
                }, ct);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }
}