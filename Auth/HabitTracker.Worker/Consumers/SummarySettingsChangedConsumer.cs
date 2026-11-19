using HabitTracker.Contracts;
using MassTransit;

namespace HabitTracker.Worker.Consumers;

public sealed class SummarySettingsChangedConsumer : IConsumer<SummarySettingsChangedV1>
{
    public Task Consume(ConsumeContext<SummarySettingsChangedV1> context) => Task.CompletedTask;
}