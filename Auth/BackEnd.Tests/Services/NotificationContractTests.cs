using System.Diagnostics;
using HabitTracker.Contracts;
using HabitTracker.Messaging;

namespace BackEnd.Tests.Services;

public sealed class NotificationContractTests
{
    [Fact]
    public void EachV1EventPreservesItsWireFieldsWhenStoredForPublication()
    {
        var eventId = Guid.Parse("77d139b2-7711-4d04-bca4-0981ab010e74");
        var correlationId = Guid.Parse("a6582672-d214-492e-aa67-937a55f0c929");
        var notificationId = Guid.Parse("ff9ecc83-19ab-4357-b9a9-df8c3be44a8f");
        var utc = new DateTime(2026, 11, 15, 13, 50, 0, DateTimeKind.Utc);
        const string userId = "65aa00000000000000000001";
        const string habitId = "65aa00000000000000000011";
        using var activity = new Activity("save").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "tests=producer";
        var context = EventContext.Capture(correlationId);

        var habit = new HabitChangedV1(eventId, utc, correlationId, userId, habitId, 7,
            "reminderChanged", "active", "2026-10-11", [new("2026-10-11", [1, 3, 7])], "Asia/Yekaterinburg", true, "20:00");
        var savedHabit = Verify(habit, "userId", "habitId", "revision", "change", "status", "startDate",
            "scheduleVersions", "timeZoneId", "reminderEnabled", "reminderLocalTime").Read<HabitChangedV1>();
        Assert.Equal("20:00", savedHabit.ReminderLocalTime);
        Assert.Equal(7L, savedHabit.Revision);
        var version = Assert.Single(savedHabit.ScheduleVersions);
        Assert.Equal("2026-10-11", version.EffectiveFrom);
        Assert.Equal(new[] { 1, 3, 7 }, version.DaysOfWeek);

        var summary = new SummarySettingsChangedV1(eventId, utc, correlationId, userId, 8, true, "21:00", "Asia/Yekaterinburg");
        Assert.Equal(summary, Verify(summary, "userId", "revision", "enabled", "localTime", "timeZoneId")
            .Read<SummarySettingsChangedV1>());

        var ready = new NotificationReadyV1(eventId, utc, correlationId, notificationId, userId,
            "dailySummary", "2026-11-15", utc, Planned: 2, Completed: 1, Pending: 1);
        var fields = new[] { "notificationId", "userId", "kind", "localDate", "createdAtUtc", "habitId", "habitName",
            "planned", "completed", "pending" };
        Assert.Equal(ready, Verify(ready, fields).Read<NotificationReadyV1>());
        var reminder = new NotificationReadyV1(eventId, utc, correlationId, notificationId, userId, "habitReminder",
            "2026-11-15", utc, habitId, "Чтение");
        Assert.Equal(reminder, Verify(reminder, fields).Read<NotificationReadyV1>());

        OutboxEvent Verify<T>(T message, params string[] fields) where T : class, IVersionedEvent
        {
            var stored = OutboxEvent.Create(message, context);
            Assert.Equal(eventId, stored.Id);
            Assert.Equal(eventId, stored.EventId);
            Assert.Equal(context, stored.Context);
            Assert.Equal(fields.Concat(["eventId", "occurredAtUtc", "correlationId"]).Order(),
                stored.Payload.Names.Order());
            Assert.Equal("2026-11-15T13:50:00Z", stored.Payload["occurredAtUtc"].AsString);
            return stored;
        }
    }
}
