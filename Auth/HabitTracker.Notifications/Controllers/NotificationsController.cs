using HabitTracker.Notifications.Contracts;
using HabitTracker.Notifications.Data;
using HabitTracker.Notifications.Hubs;
using HabitTracker.Notifications.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HabitTracker.Notifications.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController(NotificationRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationPage>> History(CancellationToken ct, [FromQuery] int limit = 20)
    {
        if (limit is < 1 or > 100 || Request.Query["limit"].Count > 1)
            return Invalid("Параметр limit должен быть целым числом от 1 до 100.");

        NotificationCursor? cursor = null;
        if (Request.Query.TryGetValue("cursor", out var values))
        {
            if (values.Count != 1 || !NotificationCursor.TryDecode(values.ToString(), out var parsed))
                return Invalid("Некорректный курсор истории.");
            cursor = parsed;
        }

        return await repository.HistoryAsync(SubjectUserIdProvider.GetUserId(User)!, limit, cursor, ct);
    }

    [HttpPut("{id}/read")]
    public async Task<IActionResult> MarkRead(string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var uuid) || uuid == Guid.Empty)
            return Invalid("Некорректный идентификатор уведомления.");

        return await repository.MarkReadAsync(SubjectUserIdProvider.GetUserId(User)!, uuid.ToString("D"), ct)
            ? NoContent()
            : ApiProblem(404, "Уведомление не найдено.", "not_found");
    }

    private ObjectResult Invalid(string message) => ApiProblem(400, message, "validation_error");

    private ObjectResult ApiProblem(int status, string title, string code)
    {
        var result = Problem(statusCode: status, title: title);
        ((ProblemDetails)result.Value!).Extensions["code"] = code;
        return result;
    }
}
