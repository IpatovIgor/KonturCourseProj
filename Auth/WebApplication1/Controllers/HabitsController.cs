using System.IdentityModel.Tokens.Jwt;
using BackEnd.Models;
using BackEnd.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace BackEnd.Controllers;

[ApiController]
[Authorize]
[Route("api/habits")]
public sealed class HabitsController(HabitService habits) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(HabitResponse), StatusCodes.Status201Created)]
    public Task<IActionResult> Create(HabitRequest request, CancellationToken ct) =>
        RunAsync(async owner =>
        {
            var habit = await habits.CreateAsync(owner, request, ct);
            return Created($"/api/habits/{habit.Id}", habit);
        }, ct);

    [HttpGet]
    [ProducesResponseType(typeof(HabitPage), StatusCodes.Status200OK)]
    public Task<IActionResult> List(CancellationToken ct,
        [FromQuery] string? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        RunAsync(async owner => Ok(await habits.ListAsync(owner,
            PreserveEmptyQuery("status", status) ?? "active", page, pageSize, ct)), ct);

    [HttpGet("today")]
    [ProducesResponseType(typeof(TodayHabitsResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Today(CancellationToken ct) =>
        RunAsync(async owner => Ok(await habits.TodayAsync(owner, ct)), ct);

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(HabitResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Get(string id, CancellationToken ct) =>
        RunAsync(async owner => Ok(await habits.GetAsync(owner, ParseId(id), ct)), ct);

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(HabitResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Edit(string id, HabitRequest request, CancellationToken ct) =>
        RunAsync(async owner => Ok(await habits.EditAsync(owner, ParseId(id), request, ct)), ct);

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Archive(string id, CancellationToken ct) =>
        RunAsync(async owner =>
        {
            await habits.ArchiveAsync(owner, ParseId(id), ct);
            return NoContent();
        }, ct);

    [HttpPut("{id}/completions/{date}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Complete(string id, string date, CancellationToken ct) =>
        ChangeCompletionAsync(id, date, true, ct);

    [HttpDelete("{id}/completions/{date}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> UndoCompletion(string id, string date, CancellationToken ct) =>
        ChangeCompletionAsync(id, date, false, ct);

    [HttpGet("{id}/history")]
    [ProducesResponseType(typeof(HabitHistoryResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> History(string id, [FromQuery] string? from, [FromQuery] string? to, CancellationToken ct) =>
        RunAsync(async owner => Ok(await habits.HistoryAsync(owner, ParseId(id),
            PreserveEmptyQuery("from", from), PreserveEmptyQuery("to", to), ct)), ct);

    [HttpGet("{id}/statistics")]
    [ProducesResponseType(typeof(HabitStatisticsResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Statistics(string id, [FromQuery] string? from, [FromQuery] string? to, CancellationToken ct) =>
        RunAsync(async owner => Ok(await habits.StatisticsAsync(owner, ParseId(id),
            PreserveEmptyQuery("from", from), PreserveEmptyQuery("to", to), ct)), ct);

    private Task<IActionResult> ChangeCompletionAsync(string id, string date, bool completed, CancellationToken ct) =>
        RunAsync(async owner =>
        {
            await habits.SetCompletionAsync(owner, ParseId(id), date, completed, ct);
            return NoContent();
        }, ct);

    private async Task<IActionResult> RunAsync(Func<User, Task<IActionResult>> action, CancellationToken ct)
    {
        try
        {
            var owner = await habits.GetOwnerAsync(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, ct);
            return await action(owner);
        }
        catch (HabitApiException error)
        {
            if (error.Status == StatusCodes.Status401Unauthorized)
                Response.Headers.WWWAuthenticate = "Bearer";

            return new ObjectResult(ToProblem(error))
            {
                StatusCode = error.Status,
                ContentTypes = { "application/problem+json" }
            };
        }
    }

    internal static ProblemDetails ToProblem(HabitApiException error) => new()
    {
        Status = error.Status,
        Title = error.Message,
        Extensions = { ["code"] = error.Code }
    };

    private string? PreserveEmptyQuery(string key, string? value) =>
        value ?? (Request.Query.ContainsKey(key) ? "" : null);

    private static ObjectId ParseId(string value) =>
        ObjectId.TryParse(value, out var id) ? id : throw HabitApiException.Validation("Некорректный ID привычки");
}
