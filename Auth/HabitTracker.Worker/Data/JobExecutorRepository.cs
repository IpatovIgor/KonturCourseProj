using HabitTracker.Worker.Models;
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class JobExecutorRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<Job> _jobs = database.GetCollection<Job>("jobs");

    // Захват: pending с DueAtUtc <= now ИЛИ running с истёкшим lease.
    public Task<Job?> ClaimAsync(string owner, DateTime now, TimeSpan lease, CancellationToken ct)
    {
        var claimable = Builders<Job>.Filter.Or(
            Builders<Job>.Filter.And(
                Builders<Job>.Filter.Eq(x => x.Status, JobStatus.Pending),
                Builders<Job>.Filter.Lte(x => x.DueAtUtc, now)),
            Builders<Job>.Filter.And(
                Builders<Job>.Filter.Eq(x => x.Status, JobStatus.Running),
                Builders<Job>.Filter.Lte(x => x.LeaseUntil, now)));

        var update = Builders<Job>.Update
            .Set(x => x.Status, JobStatus.Running)
            .Set(x => x.LeaseOwner, owner)
            .Set(x => x.LeaseUntil, now + lease)
            .Inc(x => x.LeaseVersion, 1)
            .Inc(x => x.Attempts, 1)
            .Set(x => x.LastUpdatedAtUtc, now);

        return _jobs.FindOneAndUpdateAsync(claimable, update, new()
        {
            Sort = Builders<Job>.Sort.Ascending(x => x.DueAtUtc).Ascending(x => x.Id),
            ReturnDocument = ReturnDocument.After
        }, ct)!;
    }

    // Продление lease. Возвращает true, если владение подтверждено.
    public async Task<bool> RenewLeaseAsync(Guid id, string owner, int leaseVersion, DateTime now, TimeSpan lease, CancellationToken ct)
    {
        var filter = OwnedRunning(id, owner, leaseVersion, now);
        var update = Builders<Job>.Update.Set(x => x.LeaseUntil, now + lease);
        var result = await _jobs.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    // Терминальный переход (completed/skipped/failed). Fenced.
    public async Task<bool> FinishAsync(
        IClientSessionHandle session, Guid id, string owner, int leaseVersion, DateTime now,
        string status, string? errorCode, CancellationToken ct)
    {
        var filter = OwnedRunning(id, owner, leaseVersion, now);
        var update = Builders<Job>.Update
            .Set(x => x.Status, status)
            .Set(x => x.LastErrorCode, errorCode)
            .Set(x => x.LastUpdatedAtUtc, now)
            .Unset(x => x.LeaseOwner)
            .Unset(x => x.LeaseUntil);
        var result = await _jobs.UpdateOneAsync(session, filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    // Возврат в pending на новый срок (notDueYet / временный сбой). Fenced.
    public async Task<bool> RescheduleAsync(
        IClientSessionHandle session, Guid id, string owner, int leaseVersion, DateTime now,
        DateTime nextDueAtUtc, int? httpFailureCount, string? errorCode, CancellationToken ct)
    {
        var filter = OwnedRunning(id, owner, leaseVersion, now);
        var update = Builders<Job>.Update
            .Set(x => x.Status, JobStatus.Pending)
            .Set(x => x.DueAtUtc, nextDueAtUtc)
            .Set(x => x.LastErrorCode, errorCode)
            .Set(x => x.LastUpdatedAtUtc, now)
            .Unset(x => x.LeaseOwner)
            .Unset(x => x.LeaseUntil);
        if (httpFailureCount is { } count)
            update = update.Set(x => x.HttpFailureCount, count);
        var result = await _jobs.UpdateOneAsync(session, filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    private static FilterDefinition<Job> OwnedRunning(Guid id, string owner, int leaseVersion, DateTime now) =>
        Builders<Job>.Filter.And(
            Builders<Job>.Filter.Eq(x => x.Id, id),
            Builders<Job>.Filter.Eq(x => x.Status, JobStatus.Running),
            Builders<Job>.Filter.Eq(x => x.LeaseOwner, owner),
            Builders<Job>.Filter.Eq(x => x.LeaseVersion, leaseVersion),
            Builders<Job>.Filter.Gt(x => x.LeaseUntil, now));
}