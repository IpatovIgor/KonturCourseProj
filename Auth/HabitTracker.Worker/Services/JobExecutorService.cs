using System.Globalization;
using HabitTracker.Contracts;
using HabitTracker.Messaging;
using HabitTracker.Worker.Clients;
using HabitTracker.Worker.Configuration;
using HabitTracker.Worker.Data;
using HabitTracker.Worker.Models;
using Microsoft.Extensions.Options;

namespace HabitTracker.Worker.Services;

public sealed class JobExecutorService(
    JobExecutorRepository jobs,
    MongoOutbox outbox,
    MongoTransactions transactions,
    WorkerApiClient api,
    ScheduleRepository schedules,
    BootstrapReadiness readiness,
    IOptions<WorkerOptions> options,
    TimeProvider clock,
    ILogger<JobExecutorService> logger) : BackgroundService
{
    internal string Owner { get; } = JobLease.NewOwner();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await readiness.WaitReadyAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await jobs.ClaimAsync(Owner, clock.GetUtcNow().UtcDateTime,
                    TimeSpan.FromSeconds(options.Value.LeaseSeconds), stoppingToken);
                if (job is null)
                    await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), clock, stoppingToken);
                else
                    await ExecuteOneAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Executor iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
    }

    internal async Task ExecuteOneAsync(Job job, CancellationToken stoppingToken)
    {
        using var lease = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseAsync(job, lease);
        var started = clock.GetTimestamp();
        try
        {
            await ExecuteOwnedAsync(job, lease.Token);
        }
        catch (LeaseLostException)
        {
            logger.LogWarning("Job {JobId} lost lease", job.Id);
        }
        catch (OperationCanceledException) when (lease.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} cancelled after loss of lease", job.Id);
        }
        finally
        {
            await lease.CancelAsync();
            await renewal;
            logger.LogDebug("Job {JobId} ({Kind}), correlation {CorrelationId}, took {ElapsedMs} ms",
                job.Id, job.Kind, job.LastCorrelationId, clock.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task RenewLeaseAsync(Job job, CancellationTokenSource lease)
    {
        var interval = TimeSpan.FromSeconds(options.Value.LeaseRenewalSeconds);
        var duration = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
        try
        {
            while (!lease.IsCancellationRequested)
            {
                await Task.Delay(interval, clock, lease.Token);
                if (await jobs.RenewLeaseAsync(job.Id, Owner, job.LeaseVersion, clock.GetUtcNow().UtcDateTime,
                    duration, lease.Token))
                    continue;

                await lease.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (lease.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not renew lease for job {JobId}", job.Id);
            await lease.CancelAsync();
        }
    }

    private async Task ExecuteOwnedAsync(Job job, CancellationToken ct)
    {
        if (job.Kind == JobKind.DailySummary && !options.Value.DailySummaryEnabled)
        {
            await FinishAsync(job, JobStatus.Skipped, "daily_summary_disabled", ct);
            return;
        }

        var zone = await GetTimeZoneAsync(job, ct);
        var requestStarted = clock.GetUtcNow();
        if (!IsJobDate(job, zone, requestStarted))
        {
            await FinishAsync(job, JobStatus.Skipped, "stale_local_date", ct);
            return;
        }

        Eligibility result;
        try
        {
            result = await ReadEligibilityAsync(job, ct);
        }
        catch (WorkerApiNotFoundException)
        {
            await FinishAsync(job, JobStatus.Skipped, "not_found", ct);
            return;
        }
        catch (WorkerApiUnauthorizedException)
        {
            logger.LogError("Worker API rejected the configured key for job {JobId}", job.Id);
            await FinishAsync(job, JobStatus.Failed, "unauthorized", ct);
            return;
        }
        catch (WorkerApiContractException ex)
        {
            await FinishAsync(job, JobStatus.Failed, ex.Message, ct);
            return;
        }
        catch (WorkerApiTransientException ex)
        {
            zone = await GetTimeZoneAsync(job, ct);
            var now = clock.GetUtcNow();
            var failures = job.HttpFailureCount + 1;
            if (!IsJobDate(job, zone, now))
                await FinishAsync(job, JobStatus.Skipped, "stale_local_date", ct, failures);
            else if (failures >= JobLease.MaxTransientHttpFailures)
                await FinishAsync(job, JobStatus.Failed, ex.Message, ct, failures);
            else
                await RetryAsync(job, zone, now + JobLease.Backoff(failures), ex.Message, ct, failures);
            return;
        }

        zone = await GetTimeZoneAsync(job, ct);
        var checkedAt = clock.GetUtcNow();
        if (!IsJobDate(job, zone, checkedAt))
            await FinishAsync(job, JobStatus.Skipped, "stale_local_date", ct);
        else if (result.Notification is not null)
            await CompleteAsync(job, result.Notification, ct, result.Revision, zone);
        else if (result.NextDueAt is { } next)
        {
            if (next <= requestStarted)
                await FinishAsync(job, JobStatus.Failed, "invalid_next_due", ct);
            else
                await RetryAsync(job, zone, next, "notDueYet", ct, revision: result.Revision);
        }
        else
            await FinishAsync(job, JobStatus.Skipped, "ineligible", ct, revision: result.Revision,
                scheduledDueAtUtc: result.ScheduledDueAtUtc);
    }

    private async Task<Eligibility> ReadEligibilityAsync(Job job, CancellationToken ct)
    {
        if (job.Kind == JobKind.HabitReminder)
        {
            var answer = await api.ReminderAsync(job.UserId, job.HabitKey, job.LocalDate, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            return new(answer.Revision, answer.NextDueAtUtc, answer.Eligible
                ? new NotificationReadyV1(Guid.NewGuid(), now, Correlation(job), job.Id, job.UserId,
                    job.Kind, job.LocalDate, now, HabitId: job.HabitKey, HabitName: answer.HabitName)
                : null, answer.ScheduledDueAtUtc?.UtcDateTime);
        }

        var summary = await api.SummaryAsync(job.UserId, job.LocalDate, ct);
        var created = clock.GetUtcNow().UtcDateTime;
        return new(summary.Revision, summary.NextDueAtUtc, summary.Eligible
            ? new NotificationReadyV1(Guid.NewGuid(), created, Correlation(job), job.Id, job.UserId,
                job.Kind, job.LocalDate, created, Planned: summary.Planned, Completed: summary.Completed, Pending: summary.Pending)
            : null);
    }

    private async Task<string> GetTimeZoneAsync(Job job, CancellationToken ct) =>
        (job.Kind == JobKind.HabitReminder
            ? await schedules.HabitTimeZoneAsync(job.HabitKey, ct)
            : await schedules.SummaryTimeZoneAsync(job.UserId, ct)) ?? job.TimeZoneId;

    private static bool IsJobDate(Job job, string zone, DateTimeOffset time) =>
        NotificationCalendar.LocalDate(time, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == job.LocalDate;

    private Task RetryAsync(Job job, string zone, DateTimeOffset due, string reason, CancellationToken ct,
        int? failures = null, long? revision = null) =>
        IsJobDate(job, zone, due) && IsJobDate(job, zone, clock.GetUtcNow())
            ? RescheduleAsync(job, due.UtcDateTime, failures, reason, ct, revision)
            : FinishAsync(job, JobStatus.Skipped, "stale_local_date", ct, failures, revision);

    internal async Task CompleteAsync(Job job, NotificationReadyV1 notification, CancellationToken ct,
        long? revision = null, string? zone = null)
    {
        var status = await transactions.RunAsync(async session =>
        {
            var now = clock.GetUtcNow();
            var currentStatus = zone is null || IsJobDate(job, zone, now) ? JobStatus.Completed : JobStatus.Skipped;
            var finished = await jobs.FinishAsync(session, job.Id, Owner, job.LeaseVersion, now.UtcDateTime,
                currentStatus, currentStatus == JobStatus.Skipped ? "stale_local_date" : null, ct, revision: revision);
            if (!finished)
                throw new LeaseLostException();

            if (currentStatus == JobStatus.Completed)
                await outbox.InsertAsync(session, notification,
                    new EventContext(notification.CorrelationId, job.LastTraceParent, job.LastTraceState), ct);
            return currentStatus;
        }, ct);
        LogTransition(job, status);
    }

    private async Task FinishAsync(Job job, string status, string reason, CancellationToken ct,
        int? failures = null, long? revision = null, DateTime? scheduledDueAtUtc = null)
    {
        await transactions.RunAsync(async session =>
        {
            var finished = await jobs.FinishAsync(session, job.Id, Owner, job.LeaseVersion,
                clock.GetUtcNow().UtcDateTime, status, reason, ct, failures, revision, scheduledDueAtUtc);
            if (!finished)
                throw new LeaseLostException();
            return true;
        }, ct);
        LogTransition(job, status);
    }

    private async Task RescheduleAsync(Job job, DateTime due, int? failures, string reason, CancellationToken ct, long? revision)
    {
        await transactions.RunAsync(async session =>
        {
            var updated = await jobs.RescheduleAsync(session, job.Id, Owner, job.LeaseVersion,
                clock.GetUtcNow().UtcDateTime, due, failures, reason, ct, revision);
            if (!updated)
                throw new LeaseLostException();
            return true;
        }, ct);
        LogTransition(job, JobStatus.Pending);
    }

    private void LogTransition(Job job, string status) =>
        logger.LogInformation("Job {JobId} ({Kind}), correlation {CorrelationId}, transitioned to {Status}",
            job.Id, job.Kind, job.LastCorrelationId, status);

    private static Guid Correlation(Job job) =>
        job.LastCorrelationId is { } id && id != Guid.Empty ? id : Guid.NewGuid();

    private sealed record Eligibility(long Revision, DateTimeOffset? NextDueAt, NotificationReadyV1? Notification,
        DateTime? ScheduledDueAtUtc = null);
}
