using System.Diagnostics;
using MassTransit;

namespace HabitTracker.Messaging;

public sealed record EventContext(Guid CorrelationId, string? TraceParent, string? TraceState)
{
    public static EventContext Capture(Guid correlationId)
    {
        var activity = Activity.Current;
        return new(correlationId, activity?.IdFormat == ActivityIdFormat.W3C ? activity.Id : null,
            activity?.TraceStateString);
    }

    public void Apply<T>(SendContext<T> context, Guid eventId) where T : class
    {
        context.MessageId = eventId;
        context.CorrelationId = CorrelationId;
        if (TraceParent is not null)
            context.Headers.Set("traceparent", TraceParent);
        if (TraceState is not null)
            context.Headers.Set("tracestate", TraceState);
    }
}
