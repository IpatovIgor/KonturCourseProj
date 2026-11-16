using System.Globalization;
using HabitTracker.Contracts;
using HabitTracker.Notifications.Data;
using HabitTracker.Notifications.Models;
using HabitTracker.Notifications.Services;
using MassTransit;
using MongoDB.Bson;

namespace HabitTracker.Notifications.Consumers;

public sealed class NotificationReadyConsumer(NotificationRepository repository, INotificationDelivery delivery)
    : IConsumer<NotificationReadyV1>
{
    public async Task Consume(ConsumeContext<NotificationReadyV1> context)
    {
        var notification = Validate(context.Message);
        var saved = await repository.StoreAsync(notification, context.CancellationToken);
        try
        {
            await delivery.SendAsync(saved, context.CancellationToken);
        }
        catch (Exception error) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new NotificationDeliveryException(error);
        }
    }

    internal static Notification Validate(NotificationReadyV1 message)
    {
        if (message.NotificationId == Guid.Empty || message.EventId == Guid.Empty || message.CorrelationId == Guid.Empty ||
            !ObjectId.TryParse(message.UserId, out var owner) || message.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            message.OccurredAtUtc.Kind != DateTimeKind.Utc ||
            !DateOnly.TryParseExact(message.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new NotificationContractException("Invalid notification identifiers or dates.");

        var reminder = message.Kind == "habitReminder";
        ObjectId habitId = default;
        if (reminder)
        {
            if (!ObjectId.TryParse(message.HabitId, out habitId) || string.IsNullOrWhiteSpace(message.HabitName))
                throw new NotificationContractException("Invalid reminder payload.");
        }
        else if (message.Kind != "dailySummary" ||
                 message.Planned is not >= 0 || message.Completed is not >= 0 || message.Pending is not >= 0 ||
                 message.Planned - message.Completed != message.Pending)
        {
            throw new NotificationContractException("Unknown notification kind or invalid summary payload.");
        }

        return new Notification
        {
            Id = message.NotificationId.ToString("D"),
            UserId = owner.ToString(),
            Kind = message.Kind,
            LocalDate = message.LocalDate,
            CreatedAtUtc = message.CreatedAtUtc,
            HabitId = reminder ? habitId.ToString() : null,
            HabitName = reminder ? message.HabitName : null,
            Planned = reminder ? null : message.Planned,
            Completed = reminder ? null : message.Completed,
            Pending = reminder ? null : message.Pending
        };
    }
}

public sealed class NotificationContractException(string message) : Exception(message);

public sealed class NotificationDeliveryException(Exception cause)
    : Exception("SignalR notification delivery failed.", cause);
