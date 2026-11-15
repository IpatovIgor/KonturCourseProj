using HabitTracker.Messaging;

namespace BackEnd.Services;

public sealed class RequestEventContext(IHttpContextAccessor accessor)
{
    public EventContext Capture()
    {
        var http = accessor.HttpContext;
        var correlation = Guid.NewGuid();
        if (http is not null)
        {
            if (Guid.TryParse(http.Response.Headers["X-Correlation-ID"], out var previous))
                correlation = previous;
            else if (Guid.TryParse(http.Request.Headers["X-Correlation-ID"], out var supplied))
                correlation = supplied;
            http.Response.Headers["X-Correlation-ID"] = correlation.ToString();
        }
        return EventContext.Capture(correlation);
    }
}
