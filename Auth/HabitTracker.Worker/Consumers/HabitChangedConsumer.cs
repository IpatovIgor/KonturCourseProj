using HabitTracker.Contracts;
using MassTransit;

namespace HabitTracker.Worker.Consumers;

public sealed class HabitChangedConsumer : IConsumer<HabitChangedV1>
{
    public Task Consume(ConsumeContext<HabitChangedV1> context) => Task.CompletedTask;
}