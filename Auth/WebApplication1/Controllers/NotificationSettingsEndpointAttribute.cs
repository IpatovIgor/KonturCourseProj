using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace BackEnd.Controllers;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class NotificationSettingsEndpointAttribute(bool dailySummary = false) : Attribute
{
    public bool DailySummary { get; } = dailySummary;
}

internal sealed class HideNotificationSettings(bool notificationsEnabled, bool dailySummaryEnabled) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            var settingsActions = controller.Actions
                .Where(action => action.Attributes.OfType<NotificationSettingsEndpointAttribute>()
                    .Any(attribute => !notificationsEnabled || attribute.DailySummary && !dailySummaryEnabled))
                .ToArray();
            foreach (var action in settingsActions)
                controller.Actions.Remove(action);
        }
    }
}
