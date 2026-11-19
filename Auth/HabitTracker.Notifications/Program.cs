using HabitTracker.Notifications;

var app = NotificationsApplication.Build(WebApplication.CreateBuilder(args));
await app.RunAsync();
