using HabitTracker.Contracts;
using HabitTracker.Notifications.Consumers;
using HabitTracker.Notifications.Contracts;

namespace HabitTracker.Notifications.Tests;

public sealed class NotificationPayloadTests
{
    private static readonly DateTime Utc = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static NotificationReadyV1 Event => new(Guid.NewGuid(), Utc, Guid.NewGuid(), Guid.NewGuid(),
        "507F1F77BCF86CD799439011", "dailySummary", "2026-11-15", Utc, Planned: 0, Completed: 0, Pending: 0);

    [Fact]
    public void ReminderUsesCanonicalIdsAndIgnoresSummaryCounters()
    {
        var message = Event with { Kind = "habitReminder", HabitId = "507F1F77BCF86CD799439012", HabitName = "Чтение" };
        var document = NotificationReadyConsumer.Validate(message);
        var dto = NotificationDto.From(document);

        Assert.Equal("507f1f77bcf86cd799439011", document.UserId);
        Assert.Equal(message.NotificationId.ToString(), dto.Id);
        Assert.Equal("507f1f77bcf86cd799439012", dto.HabitId);
        Assert.Equal(message.HabitName, dto.HabitName);
        Assert.Null(dto.Planned);
        Assert.Null(dto.ReadAtUtc);
    }

    [Fact]
    public void ZeroCountSummaryIsValidAndIgnoresHabitPayload()
    {
        var dto = NotificationDto.From(NotificationReadyConsumer.Validate(Event with { HabitName = "unused" }));

        Assert.Equal(0, dto.Planned);
        Assert.Equal(0, dto.Completed);
        Assert.Equal(0, dto.Pending);
        Assert.Null(dto.HabitName);
    }

    [Fact]
    public void ContractRejectsInvalidIdentityPayloadOrTime()
    {
        var value = Event;
        foreach (var invalid in new[]
        {
            value with { Kind = "unknown" },
            value with { NotificationId = Guid.Empty },
            value with { EventId = Guid.Empty },
            value with { CorrelationId = Guid.Empty },
            value with { UserId = "invalid" },
            value with { LocalDate = "2026-02-30" },
            value with { CreatedAtUtc = DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified) },
            value with { OccurredAtUtc = DateTime.SpecifyKind(Utc, DateTimeKind.Local) },
            value with { Kind = "habitReminder" },
            value with { Kind = "habitReminder", HabitId = value.UserId, HabitName = " " },
            value with { Planned = null },
            value with { Completed = -1 },
            value with { Pending = -1 },
            value with { Planned = 10, Completed = 2, Pending = 4 }
        })
        {
            Assert.Throws<NotificationContractException>(() => NotificationReadyConsumer.Validate(invalid));
        }
    }
}
