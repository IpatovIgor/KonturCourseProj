using HabitTracker.Worker.Contracts;
using HabitTracker.Worker.Models;
using HabitTracker.Worker.Scheduling;
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class ScheduleProjector(ScheduleCalculator calculator)
{
    public async Task<bool> ApplyHabitAsync(
        IClientSessionHandle session,
        IMongoCollection<HabitSchedule> collection,
        HabitScheduleSnapshot snapshot,
        Guid eventId, Guid correlationId, string? traceParent,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = await collection.Find(session, Builders<HabitSchedule>.Filter.Eq(x => x.Id, snapshot.HabitId))
            .FirstOrDefaultAsync(ct);
        if (existing is not null && snapshot.Revision <= existing.Revision)
            return false;

        var model = ToModel(snapshot, eventId, correlationId, traceParent);
        var due = calculator.NextHabitDue(model, now, afterLocalDate: null);
        model = due is null
            ? WithDue(model, null, null)
            : WithDue(model, due.DueAtUtc, due.LocalDate);

        await collection.ReplaceOneAsync(session,
            Builders<HabitSchedule>.Filter.Eq(x => x.Id, model.Id),
            model, new ReplaceOptions { IsUpsert = true }, ct);
        return true;
    }

    public async Task<bool> ApplySummaryAsync(
        IClientSessionHandle session,
        IMongoCollection<SummarySchedule> collection,
        SummaryScheduleSnapshot snapshot,
        Guid eventId, Guid correlationId, string? traceParent,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = await collection.Find(session, Builders<SummarySchedule>.Filter.Eq(x => x.Id, snapshot.UserId))
            .FirstOrDefaultAsync(ct);
        if (existing is not null && snapshot.Revision <= existing.Revision)
            return false;

        var model = new SummarySchedule
        {
            Id = snapshot.UserId,
            Revision = snapshot.Revision,
            Enabled = snapshot.Enabled,
            LocalTime = snapshot.LocalTime,
            TimeZoneId = snapshot.TimeZoneId,
            LastEventId = eventId,
            LastCorrelationId = correlationId,
            LastTraceParent = traceParent
        };
        var due = calculator.NextSummaryDue(model, now, afterLocalDate: null);
        if (due is not null)
            model = WithDue(model, due.DueAtUtc, due.LocalDate);

        await collection.ReplaceOneAsync(session,
            Builders<SummarySchedule>.Filter.Eq(x => x.Id, model.Id),
            model, new ReplaceOptions { IsUpsert = true }, ct);
        return true;
    }

    private static HabitSchedule ToModel(HabitScheduleSnapshot s, Guid eventId, Guid correlationId, string? traceParent) => new()
    {
        Id = s.HabitId,
        UserId = s.UserId,
        Revision = s.Revision,
        Status = s.Status,
        StartDate = s.StartDate,
        ScheduleVersions = s.ScheduleVersions
            .Select(v => new ScheduleVersion { EffectiveFrom = v.EffectiveFrom, DaysOfWeek = v.DaysOfWeek })
            .ToArray(),
        TimeZoneId = s.TimeZoneId,
        ReminderEnabled = s.ReminderEnabled,
        ReminderLocalTime = s.ReminderLocalTime,
        LastEventId = eventId,
        LastCorrelationId = correlationId,
        LastTraceParent = traceParent
    };

    private static HabitSchedule WithDue(HabitSchedule h, DateTime? dueAtUtc, string? localDate) => new()
    {
        Id = h.Id, UserId = h.UserId, Revision = h.Revision, Status = h.Status, StartDate = h.StartDate,
        ScheduleVersions = h.ScheduleVersions, TimeZoneId = h.TimeZoneId, ReminderEnabled = h.ReminderEnabled,
        ReminderLocalTime = h.ReminderLocalTime, NextDueAtUtc = dueAtUtc, NextLocalDate = localDate,
        LastEventId = h.LastEventId, LastCorrelationId = h.LastCorrelationId, LastTraceParent = h.LastTraceParent
    };

    private static SummarySchedule WithDue(SummarySchedule s, DateTime dueAtUtc, string localDate) => new()
    {
        Id = s.Id, Revision = s.Revision, Enabled = s.Enabled, LocalTime = s.LocalTime,
        TimeZoneId = s.TimeZoneId, NextDueAtUtc = dueAtUtc, NextLocalDate = localDate,
        LastEventId = s.LastEventId, LastCorrelationId = s.LastCorrelationId, LastTraceParent = s.LastTraceParent
    };
}