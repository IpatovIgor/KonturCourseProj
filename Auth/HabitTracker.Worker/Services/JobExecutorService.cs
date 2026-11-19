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
    BootstrapReadiness readiness,
    IOptions<WorkerOptions> options,
    TimeProvider clock,
    ILogger<JobExecutorService> logger) : BackgroundService
{
    private readonly string _owner = JobLease.NewOwner();

    internal string Owner => _owner;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await readiness.WaitReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await jobs.ClaimAsync(_owner, clock.GetUtcNow().UtcDateTime,
                    TimeSpan.FromSeconds(options.Value.LeaseSeconds), stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), clock, stoppingToken);
                    continue;
                }

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

    private async Task ExecuteOneAsync(Job job, CancellationToken stoppingToken)
    {
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseAsync(job, leaseCts);

        try
        {
            if (await IsStaleLocalDateAsync(job, stoppingToken))
            {
                await SkipAsync(job, "stale_local_date", stoppingToken);
                return;
            }

            if (job.Kind == JobKind.HabitReminder)
                await ExecuteReminderAsync(job, stoppingToken);
            else
                await ExecuteSummaryAsync(job, stoppingToken);
        }
        catch (LeaseLostException)
        {
            logger.LogWarning("Job {JobId} lost lease", job.Id);
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} lost lease during execution", job.Id);
        }
        finally
        {
            leaseCts.Cancel();
            await renewal;
        }
    }

    private async Task RenewLeaseAsync(Job job, CancellationTokenSource leaseCts)
    {
        var interval = TimeSpan.FromSeconds(options.Value.LeaseRenewalSeconds);
        var lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
        while (!leaseCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, clock, leaseCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var ok = await jobs.RenewLeaseAsync(job.Id, _owner, job.LeaseVersion,
                clock.GetUtcNow().UtcDateTime, lease, leaseCts.Token);
            if (!ok)
            {
                leaseCts.Cancel();
                return;
            }
        }
    }

    private Task<bool> IsStaleLocalDateAsync(Job job, CancellationToken ct)
    {
        var today = NotificationCalendar.LocalDate(clock.GetUtcNow(), job.TimeZoneId).ToString("yyyy-MM-dd");
        return Task.FromResult(today != job.LocalDate);
    }

    private async Task ExecuteReminderAsync(Job job, CancellationToken ct)
    {
        try
        {
            var result = await api.ReminderAsync(job.UserId, job.HabitKey, job.LocalDate, ct);
            if (result.Eligible)
            {
                if (await IsStaleLocalDateAsync(job, ct))
                {
                    await SkipAsync(job, "stale_local_date", ct);
                    return;
                }

                await CompleteAsync(job, new NotificationReadyV1(
                    Guid.NewGuid(), clock.GetUtcNow().UtcDateTime, Correlation(job),
                    job.Id, job.UserId, JobKind.HabitReminder, job.LocalDate,
                    clock.GetUtcNow().UtcDateTime, HabitId: job.HabitKey, HabitName: result.HabitName), ct);
                return;
            }

            if (result.Reason == "notDueYet" && result.NextDueAtUtc is { } next)
            {
                await RescheduleAsync(job, next.UtcDateTime, httpFailureCount: null, "notDueYet", ct);
                return;
            }

            await SkipAsync(job, "ineligible", ct);
        }
        catch (WorkerApiTransientException ex)
        {
            await HandleTransientAsync(job, ex.Message, ct);
        }
        catch (WorkerApiNotFoundException)
        {
            await SkipAsync(job, "not_found", ct);
        }
        catch (WorkerApiUnauthorizedException)
        {
            logger.LogError("Worker API rejected key during job {JobId}", job.Id);
            await FailAsync(job, "unauthorized", ct);
        }
        catch (WorkerApiContractException ex)
        {
            await FailAsync(job, $"contract:{ex.Message}", ct);
        }
    }

    private async Task ExecuteSummaryAsync(Job job, CancellationToken ct)
    {
        try
        {
            var result = await api.SummaryAsync(job.UserId, job.LocalDate, ct);
            if (result.Eligible)
            {
                if (await IsStaleLocalDateAsync(job, ct))
                {
                    await SkipAsync(job, "stale_local_date", ct);
                    return;
                }

                await CompleteAsync(job, new NotificationReadyV1(
                    Guid.NewGuid(), clock.GetUtcNow().UtcDateTime, Correlation(job),
                    job.Id, job.UserId, JobKind.DailySummary, job.LocalDate,
                    clock.GetUtcNow().UtcDateTime, Planned: result.Planned, Completed: result.Completed, Pending: result.Pending), ct);
                return;
            }

            if (result.Reason == "notDueYet" && result.NextDueAtUtc is { } next)
            {
                await RescheduleAsync(job, next.UtcDateTime, httpFailureCount: null, "notDueYet", ct);
                return;
            }

            await SkipAsync(job, "ineligible", ct);
        }
        catch (WorkerApiTransientException ex)
        {
            await HandleTransientAsync(job, ex.Message, ct);
        }
        catch (WorkerApiNotFoundException)
        {
            await SkipAsync(job, "not_found", ct);
        }
        catch (WorkerApiUnauthorizedException)
        {
            logger.LogError("Worker API rejected key during job {JobId}", job.Id);
            await FailAsync(job, "unauthorized", ct);
        }
        catch (WorkerApiContractException ex)
        {
            await FailAsync(job, $"contract:{ex.Message}", ct);
        }
    }

    private async Task HandleTransientAsync(Job job, string error, CancellationToken ct)
    {
        var failures = job.HttpFailureCount + 1;
        if (failures >= JobLease.MaxTransientHttpFailures)
        {
            await FailAsync(job, error, ct);
            return;
        }

        var next = clock.GetUtcNow() + JobLease.Backoff(failures);
        await RescheduleAsync(job, next.UtcDateTime, failures, error, ct);
    }

    internal Task CompleteAsync(Job job, NotificationReadyV1 notification, CancellationToken ct) =>
        transactions.RunAsync(async session =>
        {
            var finished = await jobs.FinishAsync(session, job.Id, _owner, job.LeaseVersion,
                clock.GetUtcNow().UtcDateTime, JobStatus.Completed, null, ct);
            if (!finished)
                throw new LeaseLostException();

            await outbox.InsertAsync(session, notification, EventContext.Capture(notification.CorrelationId), ct);
            return true;
        }, ct);

    internal Task SkipAsync(Job job, string reason, CancellationToken ct) =>
        transactions.RunAsync(async session =>
        {
            var finished = await jobs.FinishAsync(session, job.Id, _owner, job.LeaseVersion,
                clock.GetUtcNow().UtcDateTime, JobStatus.Skipped, reason, ct);
            if (!finished) throw new LeaseLostException();
            return true;
        }, ct);

    private Task FailAsync(Job job, string reason, CancellationToken ct) =>
        transactions.RunAsync(async session =>
        {
            var finished = await jobs.FinishAsync(session, job.Id, _owner, job.LeaseVersion,
                clock.GetUtcNow().UtcDateTime, JobStatus.Failed, reason, ct);
            if (!finished) throw new LeaseLostException();
            return true;
        }, ct);

    internal Task RescheduleAsync(Job job, DateTime nextDueAtUtc, int? httpFailureCount, string reason, CancellationToken ct) =>
        transactions.RunAsync(async session =>
        {
            var ok = await jobs.RescheduleAsync(session, job.Id, _owner, job.LeaseVersion,
                clock.GetUtcNow().UtcDateTime, nextDueAtUtc, httpFailureCount, reason, ct);
            if (!ok) throw new LeaseLostException();
            return true;
        }, ct);

    private static Guid Correlation(Job job) => job.LastCorrelationId ?? Guid.NewGuid();
}