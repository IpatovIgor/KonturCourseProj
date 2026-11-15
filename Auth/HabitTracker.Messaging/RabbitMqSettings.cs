using HabitTracker.Contracts;
using MassTransit;

namespace HabitTracker.Messaging;

public sealed class RabbitMqSettings
{
    public string Host { get; set; } = "";
    public string VirtualHost { get; set; } = "habit-tracker";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public void Configure(IRabbitMqBusFactoryConfigurator bus)
    {
        // An AMQP URI can carry a port for local integration environments.
        var address = Host.Contains("://", StringComparison.Ordinal)
            ? new UriBuilder(Host) : new UriBuilder("rabbitmq", Host);
        address.Path = Uri.EscapeDataString(VirtualHost);
        bus.Host(address.Uri, config =>
        {
            config.Username(Username);
            config.Password(Password);
            config.PublisherConfirmation = true;
        });

        ConfigureContract<HabitChangedV1>(bus, "habit-changed-v1");
        ConfigureContract<SummarySettingsChangedV1>(bus, "summary-settings-changed-v1");
        ConfigureContract<NotificationReadyV1>(bus, "notification-ready-v1");
        bus.Publish<IVersionedEvent>(config => config.Exclude = true);
    }

    private static void ConfigureContract<T>(IRabbitMqBusFactoryConfigurator bus, string exchange) where T : class
    {
        bus.Message<T>(config => config.SetEntityName(exchange));
        bus.Publish<T>(config =>
        {
            config.ExchangeType = "fanout";
            config.Durable = true;
            config.AutoDelete = false;
        });
    }
}
