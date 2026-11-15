using System.Security.Cryptography;
using System.Text;
using BackEnd.Data;
using BackEnd.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BackEnd.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[ServiceFilter(typeof(WorkerKeyFilter))]
[Route("internal/v1")]
public sealed class WorkerController(WorkerSchedules schedules) : ControllerBase
{
    [HttpGet("habit-schedules")]
    public Task<SchedulePage<HabitScheduleSnapshot>> HabitPage(CancellationToken ct,
        [FromQuery] string? after = null, [FromQuery] int limit = 100) =>
        schedules.HabitPageAsync(Cursor(after), limit, ct);

    [HttpGet("summary-schedules")]
    public Task<SchedulePage<SummaryScheduleSnapshot>> SummaryPage(CancellationToken ct,
        [FromQuery] string? after = null, [FromQuery] int limit = 100) =>
        schedules.SummaryPageAsync(Cursor(after), limit, ct);

    [HttpGet("users/{userId}/habits/{habitId}/reminder")]
    public Task<ReminderEligibility> Reminder(string userId, string habitId, [FromQuery] string date, CancellationToken ct) =>
        schedules.ReminderAsync(WorkerSchedules.ParseId(userId), WorkerSchedules.ParseId(habitId), date, ct);

    [HttpGet("users/{userId}/summary")]
    public Task<SummaryEligibility> Summary(string userId, [FromQuery] string date, CancellationToken ct) =>
        schedules.SummaryAsync(WorkerSchedules.ParseId(userId), date, ct);

    private string? Cursor(string? after) => after ?? (Request.Query.ContainsKey("after") ? "" : null);
}

public sealed class WorkerKeyFilter(IConfiguration configuration) : IAuthorizationFilter
{
    private readonly byte[]? _expected = configuration["WorkerApi:SharedKey"] is { Length: > 0 } key
        ? Digest(key) : null;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var supplied = context.HttpContext.Request.Headers["X-Worker-Key"];
        if (_expected is null || supplied.Count != 1
            || !CryptographicOperations.FixedTimeEquals(_expected, Digest(supplied[0]!)))
            context.Result = new UnauthorizedResult();
    }

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
