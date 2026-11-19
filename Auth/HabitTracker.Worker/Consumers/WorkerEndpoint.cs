using HabitTracker.Worker.Contracts;
using MassTransit;

namespace HabitTracker.Worker.Consumers;

internal static class WorkerEndpoint
{
    public const string QueueName = "worker-habit-events-v1";

    public static void Configure(IRegistrationContext context, IRabbitMqReceiveEndpointConfigurator endpoint)
    {
        endpoint.UseMessageRetry(retry =>
        {
            retry.Ignore<InvalidScheduleException>();
            retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        });
        endpoint.ConfigureConsumer<HabitChangedConsumer>(context);
        endpoint.ConfigureConsumer<SummarySettingsChangedConsumer>(context);
    }
}
