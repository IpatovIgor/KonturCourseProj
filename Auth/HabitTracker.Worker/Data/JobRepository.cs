using HabitTracker.Worker.Models;
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class JobRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<Job> _jobs = database.GetCollection<Job>("jobs");

    public async Task UpsertPendingAsync(IClientSessionHandle session, Job job, CancellationToken ct)
    {
        var existing = await _jobs.Find(session, item => item.UserId == job.UserId && item.Kind == job.Kind &&
            item.HabitKey == job.HabitKey && item.LocalDate == job.LocalDate).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            await _jobs.InsertOneAsync(session, job, cancellationToken: ct);
            return;
        }

        if (existing.Status != JobStatus.Pending)
            return;

        var keepDelay = existing.LastErrorCode == "notDueYet"
            ? existing.ScheduleRevision >= job.ScheduleRevision
            : existing.HttpFailureCount > 0;
        var due = keepDelay && existing.DueAtUtc > job.DueAtUtc ? existing.DueAtUtc : job.DueAtUtc;
        var update = Builders<Job>.Update
            .Set(x => x.DueAtUtc, due)
            .Set(x => x.ScheduleRevision, job.ScheduleRevision)
            .Set(x => x.TimeZoneId, job.TimeZoneId)
            .Set(x => x.LastCorrelationId, job.LastCorrelationId)
            .Set(x => x.LastTraceParent, job.LastTraceParent)
            .Set(x => x.LastTraceState, job.LastTraceState)
            .Set(x => x.LastUpdatedAtUtc, job.LastUpdatedAtUtc);
        await _jobs.UpdateOneAsync(session, x => x.Id == existing.Id && x.Status == JobStatus.Pending,
            update, cancellationToken: ct);
    }
}
