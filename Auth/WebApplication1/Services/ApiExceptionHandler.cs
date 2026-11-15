using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BackEnd.Services;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var problem = exception is HabitApiException known ? ToProblem(known) : new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Внутренняя ошибка сервера"
        };
        if (problem.Status == StatusCodes.Status401Unauthorized)
            context.Response.Headers.WWWAuthenticate = "Bearer";

        context.Response.StatusCode = problem.Status!.Value;
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        problem.Extensions["traceId"] = traceId;
        if (exception is not HabitApiException)
            logger.LogError(exception, "Unhandled WebAPI error ({TraceId})", traceId);
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json",
            cancellationToken: ct);
        return true;
    }

    internal static ProblemDetails ToProblem(HabitApiException error) => new()
    {
        Status = error.Status,
        Title = error.Message,
        Extensions = { ["code"] = error.Code }
    };
}
