using HabitTracker.Worker.Models;
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class ScheduleRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<HabitSchedule> _habits = database.GetCollection<HabitSchedule>("habitSchedules");
    private readonly IMongoCollection<SummarySchedule> _summaries = database.GetCollection<SummarySchedule>("summarySchedules");

    public async Task<IReadOnlyList<HabitSchedule>> FindDueHabitsAsync(DateTime now, int limit, CancellationToken ct)
    {
        var filter = Builders<HabitSchedule>.Filter.And(
            Builders<HabitSchedule>.Filter.Ne(x => x.NextDueAtUtc, null),
            Builders<HabitSchedule>.Filter.Lte(x => x.NextDueAtUtc, now));
        return await _habits.Find(filter)
            .SortBy(x => x.NextDueAtUtc).ThenBy(x => x.Id)
            .Limit(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SummarySchedule>> FindDueSummariesAsync(DateTime now, int limit, CancellationToken ct)
    {
        var filter = Builders<SummarySchedule>.Filter.And(
            Builders<SummarySchedule>.Filter.Ne(x => x.NextDueAtUtc, null),
            Builders<SummarySchedule>.Filter.Lte(x => x.NextDueAtUtc, now));
        return await _summaries.Find(filter)
            .SortBy(x => x.NextDueAtUtc).ThenBy(x => x.Id)
            .Limit(limit)
            .ToListAsync(ct);
    }

    public Task<string?> HabitTimeZoneAsync(string id, CancellationToken ct) =>
        _habits.Find(x => x.Id == id).Project(x => x.TimeZoneId).FirstOrDefaultAsync(ct)!;

    public Task<string?> SummaryTimeZoneAsync(string id, CancellationToken ct) =>
        _summaries.Find(x => x.Id == id).Project(x => x.TimeZoneId).FirstOrDefaultAsync(ct)!;

    // CAS: сдвигаем указатель только если _id, revision и старый nextDueAtUtc не изменились.
    // Возвращает true, если сдвиг прошёл.
    public async Task<bool> TryAdvanceHabitAsync(
        IClientSessionHandle session, HabitSchedule expected, DateTime? newDueUtc, string? newLocalDate, CancellationToken ct)
    {
        var filter = Builders<HabitSchedule>.Filter.And(
            Builders<HabitSchedule>.Filter.Eq(x => x.Id, expected.Id),
            Builders<HabitSchedule>.Filter.Eq(x => x.Revision, expected.Revision),
            Builders<HabitSchedule>.Filter.Eq(x => x.NextDueAtUtc, expected.NextDueAtUtc));
        var update = Builders<HabitSchedule>.Update
            .Set(x => x.NextDueAtUtc, newDueUtc)
            .Set(x => x.NextLocalDate, newLocalDate);
        var result = await _habits.UpdateOneAsync(session, filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> TryAdvanceSummaryAsync(
        IClientSessionHandle session, SummarySchedule expected, DateTime? newDueUtc, string? newLocalDate, CancellationToken ct)
    {
        var filter = Builders<SummarySchedule>.Filter.And(
            Builders<SummarySchedule>.Filter.Eq(x => x.Id, expected.Id),
            Builders<SummarySchedule>.Filter.Eq(x => x.Revision, expected.Revision),
            Builders<SummarySchedule>.Filter.Eq(x => x.NextDueAtUtc, expected.NextDueAtUtc));
        var update = Builders<SummarySchedule>.Update
            .Set(x => x.NextDueAtUtc, newDueUtc)
            .Set(x => x.NextLocalDate, newLocalDate);
        var result = await _summaries.UpdateOneAsync(session, filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }
}