using System.Globalization;
using HabitTracker.Contracts;
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
        if (options.Value.DailySummaryEnabled)
            await ScheduleSummariesAsync(now, ct);
    }

    private async Task ScheduleHabitsAsync(DateTimeOffset now, CancellationToken ct)
    {
        HabitSchedule? after = null;
        var remaining = options.Value.BatchSize;
        while (remaining > 0)
        {
            var due = await schedules.FindDueHabitsAsync(now.UtcDateTime, remaining, ct, after);
            if (due.Count == 0)
                return;

            foreach (var habit in due)
            {
                ct.ThrowIfCancellationRequested();
                if (await TryScheduleHabitAsync(habit, now, ct))
                    remaining--;
            }

            // A held lease keeps its slot due for the next tick, but this tick can visit later slots.
            after = due[^1];
        }
    }

    private async Task<bool> TryScheduleHabitAsync(HabitSchedule habit, DateTimeOffset now, CancellationToken ct)
    {
        var current = habit;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await transactions.RunAsync(async session =>
                {
                    var dueSlot = CurrentSlot(current.NextLocalDate!, current.NextDueAtUtc!.Value, current.TimeZoneId, now)
                        ?? calculator.NextHabitDue(current, now, afterLocalDate: null);
                    var slot = dueSlot;
                    if (dueSlot is not null && dueSlot.DueAtUtc <= now.UtcDateTime)
                    {
                        if (!await jobs.UpsertPendingAsync(session,
                            NewJob(current, dueSlot.LocalDate, dueSlot.DueAtUtc, now), ct))
                            return false;
                        slot = calculator.NextHabitDue(current, now, dueSlot.LocalDate);
                    }

                    var advanced = await schedules.TryAdvanceHabitAsync(session, current,
                        slot?.DueAtUtc, slot?.LocalDate, ct);
                    if (!advanced)
                        throw new ScheduleRaceException();
                    return true;
                }, ct);
            }
            catch (ScheduleRaceException)
            {
                logger.LogInformation("Schedule race for habit {HabitId}; retrying", habit.Id);
                var fresh = await schedules.FindDueHabitAsync(habit.Id, now.UtcDateTime, ct);
                if (fresh is null)
                    break;
                current = fresh;
            }
        }
        return false;
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
                        var dueSlot = CurrentSlot(current.NextLocalDate!, current.NextDueAtUtc!.Value, current.TimeZoneId, now)
                            ?? calculator.NextSummaryDue(current, now, afterLocalDate: null);
                        var slot = dueSlot;
                        if (dueSlot is not null && dueSlot.DueAtUtc <= now.UtcDateTime)
                        {
                            await jobs.UpsertPendingAsync(session,
                                NewSummaryJob(current, dueSlot.LocalDate, dueSlot.DueAtUtc, now), ct);
                            slot = calculator.NextSummaryDue(current, now, dueSlot.LocalDate);
                        }

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

    private static DueSlot? CurrentSlot(string localDate, DateTime dueAtUtc, string zone, DateTimeOffset now) =>
        string.CompareOrdinal(localDate, NotificationCalendar.LocalDate(now, zone)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0 ? null : new DueSlot(localDate, dueAtUtc);

    private static Job NewJob(HabitSchedule habit, string localDate, DateTime dueAtUtc, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Kind = JobKind.HabitReminder,
        UserId = habit.UserId,
        HabitKey = habit.Id,
        LocalDate = localDate,
        DueAtUtc = dueAtUtc,
        ScheduledDueAtUtc = dueAtUtc,
        TimeZoneId = habit.TimeZoneId,
        ScheduleRevision = habit.Revision,
        LastCorrelationId = habit.LastCorrelationId,
        LastTraceParent = habit.LastTraceParent,
        LastTraceState = habit.LastTraceState,
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
        ScheduleRevision = summary.Revision,
        LastCorrelationId = summary.LastCorrelationId,
        LastTraceParent = summary.LastTraceParent,
        LastTraceState = summary.LastTraceState,
        Status = JobStatus.Pending,
        LastUpdatedAtUtc = now.UtcDateTime
    };
}
