using HabitTracker.Contracts;
using HabitTracker.Messaging;
using HabitTracker.Worker.Contracts;
using HabitTracker.Worker.Models;
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class ScheduleInbox(
    IMongoDatabase database, MongoTransactions transactions, ScheduleProjector projector, TimeProvider clock)
{
    public Task ApplyAsync(HabitChangedV1 message, EventContext context, CancellationToken ct)
    {
        ValidateEnvelope(message);
        if (message.Change is not ("created" or "edited" or "archived" or "reminderChanged" or "completionChanged"))
            throw new InvalidScheduleException("Unknown habit change.");
        var snapshot = new HabitScheduleSnapshot(message.UserId, message.HabitId, message.Revision, message.Status,
            message.StartDate, message.ScheduleVersions?.Select(v => v is null ? null! :
                new ScheduleVersionSnapshot(v.EffectiveFrom, v.DaysOfWeek)).ToArray()!, message.TimeZoneId,
            message.ReminderEnabled, message.ReminderLocalTime);
        ScheduleValidation.Check(snapshot);
        return ConsumeAsync(message, (session, now) => projector.ApplyHabitAsync(session,
            database.GetCollection<HabitSchedule>("habitSchedules"), snapshot, message.EventId,
            context.CorrelationId, context.TraceParent, now, ct, context.TraceState), ct);
    }

    public Task ApplyAsync(SummarySettingsChangedV1 message, EventContext context, CancellationToken ct)
    {
        ValidateEnvelope(message);
        var snapshot = new SummaryScheduleSnapshot(message.UserId, message.Revision, message.Enabled, message.LocalTime,
            message.TimeZoneId);
        ScheduleValidation.Check(snapshot);
        return ConsumeAsync(message, (session, now) => projector.ApplySummaryAsync(session,
            database.GetCollection<SummarySchedule>("summarySchedules"), snapshot, message.EventId,
            context.CorrelationId, context.TraceParent, now, ct, context.TraceState), ct);
    }

    private Task ConsumeAsync<T>(T message, Func<IClientSessionHandle, DateTimeOffset, Task<bool>> apply, CancellationToken ct)
        where T : IVersionedEvent =>
        transactions.RunAsync(async session =>
        {
            var inbox = database.GetCollection<InboxEvent>("inboxEvents");
            if (await inbox.Find(session, x => x.Id == message.EventId).AnyAsync(ct))
                return false;
            var now = clock.GetUtcNow();
            await inbox.InsertOneAsync(session, new InboxEvent
            {
                Id = message.EventId, EventType = typeof(T).Name, ProcessedAtUtc = now.UtcDateTime
            }, cancellationToken: ct);
            await apply(session, now);
            return true;
        }, ct);

    private static void ValidateEnvelope(IVersionedEvent message)
    {
        if (message.EventId == Guid.Empty || message.CorrelationId == Guid.Empty ||
            message.OccurredAtUtc == default || message.OccurredAtUtc.Kind != DateTimeKind.Utc)
            throw new InvalidScheduleException("Invalid event envelope.");
    }
}
