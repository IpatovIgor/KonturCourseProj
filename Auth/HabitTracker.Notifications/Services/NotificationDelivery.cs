using HabitTracker.Notifications.Contracts;
using HabitTracker.Notifications.Hubs;
using HabitTracker.Notifications.Models;
using Microsoft.AspNetCore.SignalR;

namespace HabitTracker.Notifications.Services;

public interface INotificationDelivery
{
    Task SendAsync(Notification notification, CancellationToken ct);
}

public sealed class NotificationDelivery(IHubContext<NotificationsHub> hub) : INotificationDelivery
{
    public Task SendAsync(Notification notification, CancellationToken ct) =>
        hub.Clients.User(notification.UserId).SendAsync(
            NotificationsHub.ReceivedEvent, NotificationDto.From(notification), ct);
}
