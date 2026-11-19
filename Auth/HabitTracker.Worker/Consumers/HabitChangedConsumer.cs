using HabitTracker.Contracts;
using HabitTracker.Messaging;
using HabitTracker.Worker.Data;
using MassTransit;

namespace HabitTracker.Worker.Consumers;

public sealed class HabitChangedConsumer(ScheduleInbox inbox) : IConsumer<HabitChangedV1>
{
    public Task Consume(ConsumeContext<HabitChangedV1> context) =>
        inbox.ApplyAsync(context.Message, new EventContext(context.Message.CorrelationId,
            context.Headers.Get<string>("traceparent"), context.Headers.Get<string>("tracestate")),
            context.CancellationToken);
}
