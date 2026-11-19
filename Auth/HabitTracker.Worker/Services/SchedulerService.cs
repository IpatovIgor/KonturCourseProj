using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using HabitTracker.Worker.Scheduling;
using Microsoft.Extensions.Options;

namespace HabitTracker.Worker.Services;

public sealed class SchedulerService(
    ScheduleRepository schedules,
    JobRepository jobs,
    ScheduleCalculator calculator,
    MongoTransactions transactions,
    BootstrapReadiness readiness,
    IOptions<WorkerOptions> options,
    TimeProvider clock,
    ILogger<SchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await readiness.WaitReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), clock, stoppingToken);
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await ScheduleHabitsAsync(now, ct);
        await ScheduleSummariesAsync(now, ct);
    }

    private async Task ScheduleHabitsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await schedules.FindDueHabitsAsync(now.UtcDateTime, options.Value.BatchSize, ct);
        foreach (var initial in due)
        {
            ct.ThrowIfCancellationRequested();
            var current = initial;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await transactions.RunAsync(async session =>
                    {
                        var slot = calculator.NextHabitDue(current, now, afterLocalDate: current.NextLocalDate);
                        var job = NewJob(current, current.NextLocalDate!, current.NextDueAtUtc!.Value, now);
                        await jobs.UpsertPendingAsync(session, job, ct);

                        var advanced = await schedules.TryAdvanceHabitAsync(session, current,
                            slot?.DueAtUtc, slot?.LocalDate, ct);
                        if (!advanced)
                            throw new ScheduleRaceException();
                        return true;
                    }, ct);
                    break;
                }
                catch (ScheduleRaceException)
                {
                    logger.LogInformation("Schedule race for habit {HabitId}; retrying", initial.Id);
                    var fresh = await schedules.FindDueHabitsAsync(now.UtcDateTime, options.Value.BatchSize, ct);
                    var match = fresh.FirstOrDefault(x => x.Id == initial.Id);
                    if (match is null)
                        break;
                    current = match;
                }
            }
        }
    }

    private async Task ScheduleSummariesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await schedules.FindDueSummariesAsync(now.UtcDateTime, options.Value.BatchSize, ct);
        foreach (var initial in due)
        {
            ct.ThrowIfCancellationRequested();
            var current = initial;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await transactions.RunAsync(async session =>
                    {
                        var slot = calculator.NextSummaryDue(current, now, afterLocalDate: current.NextLocalDate);
                        var job = NewSummaryJob(current, current.NextLocalDate!, current.NextDueAtUtc!.Value, now);
                        await jobs.UpsertPendingAsync(session, job, ct);

                        var advanced = await schedules.TryAdvanceSummaryAsync(session, current,
                            slot?.DueAtUtc, slot?.LocalDate, ct);
                        if (!advanced)
                            throw new ScheduleRaceException();
                        return true;
                    }, ct);
                    break;
                }
                catch (ScheduleRaceException)
                {
                    logger.LogInformation("Schedule race for summary {UserId}; retrying", initial.Id);
                    var fresh = await schedules.FindDueSummariesAsync(now.UtcDateTime, options.Value.BatchSize, ct);
                    var match = fresh.FirstOrDefault(x => x.Id == initial.Id);
                    if (match is null)
                        break;
                    current = match;
                }
            }
        }
    }

    private static Job NewJob(HabitSchedule habit, string localDate, DateTime dueAtUtc, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Kind = JobKind.HabitReminder,
        UserId = habit.UserId,
        HabitKey = habit.Id,
        LocalDate = localDate,
        DueAtUtc = dueAtUtc,
        TimeZoneId = habit.TimeZoneId,
        Status = JobStatus.Pending,
        LastUpdatedAtUtc = now.UtcDateTime
    };

    private static Job NewSummaryJob(SummarySchedule summary, string localDate, DateTime dueAtUtc, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Kind = JobKind.DailySummary,
        UserId = summary.Id,
        HabitKey = "",
        LocalDate = localDate,
        DueAtUtc = dueAtUtc,
        TimeZoneId = summary.TimeZoneId,
        Status = JobStatus.Pending,
        LastUpdatedAtUtc = now.UtcDateTime
    };
}