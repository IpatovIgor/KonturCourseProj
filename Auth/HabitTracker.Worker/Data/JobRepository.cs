using HabitTracker.Worker.Models;
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class JobRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<Job> _jobs = database.GetCollection<Job>("jobs");

    public async Task<bool> UpsertPendingAsync(IClientSessionHandle session, Job job, CancellationToken ct)
    {
        var existing = await _jobs.Find(session, item => item.UserId == job.UserId && item.Kind == job.Kind &&
            item.HabitKey == job.HabitKey && item.LocalDate == job.LocalDate).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            await _jobs.InsertOneAsync(session, job, cancellationToken: ct);
            return true;
        }

        if (existing.ScheduleRevision > job.ScheduleRevision)
            return true;

        // Keep a newer slot due while an executor can still finish from an older eligibility check.
        if (job.Kind == JobKind.HabitReminder && existing.Status == JobStatus.Running &&
            job.ScheduleRevision > existing.ScheduleRevision)
            return false;

        var reopen = job.Kind == JobKind.HabitReminder && existing.Status == JobStatus.Skipped &&
            existing.LastErrorCode == "ineligible" && job.ScheduleRevision > existing.ScheduleRevision &&
            job.DueAtUtc > (existing.ScheduledDueAtUtc ?? existing.DueAtUtc);
        if (existing.Status != JobStatus.Pending && !reopen)
            return true;

        var keepDelay = !reopen && (existing.LastErrorCode == "notDueYet"
            ? existing.ScheduleRevision >= job.ScheduleRevision
            : existing.HttpFailureCount > 0);
        var due = keepDelay && existing.DueAtUtc > job.DueAtUtc ? existing.DueAtUtc : job.DueAtUtc;
        var update = Builders<Job>.Update
            .Set(x => x.DueAtUtc, due)
            .Set(x => x.ScheduledDueAtUtc, job.ScheduledDueAtUtc)
            .Set(x => x.ScheduleRevision, job.ScheduleRevision)
            .Set(x => x.TimeZoneId, job.TimeZoneId)
            .Set(x => x.LastCorrelationId, job.LastCorrelationId)
            .Set(x => x.LastTraceParent, job.LastTraceParent)
            .Set(x => x.LastTraceState, job.LastTraceState)
            .Set(x => x.LastUpdatedAtUtc, job.LastUpdatedAtUtc);
        if (reopen)
            update = update.Set(x => x.Status, JobStatus.Pending)
                .Set(x => x.LastErrorCode, null).Set(x => x.HttpFailureCount, 0);

        await _jobs.UpdateOneAsync(session, x => x.Id == existing.Id && x.Status == existing.Status,
            update, cancellationToken: ct);
        return true;
    }
}
