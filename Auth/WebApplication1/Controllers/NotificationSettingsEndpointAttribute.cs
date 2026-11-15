using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace BackEnd.Controllers;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class NotificationSettingsEndpointAttribute : Attribute;

internal sealed class HideNotificationSettings : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            var settingsActions = controller.Actions
                .Where(action => action.Attributes.OfType<NotificationSettingsEndpointAttribute>().Any())
                .ToArray();
            foreach (var action in settingsActions)
                controller.Actions.Remove(action);
        }
    }
}
