using System.Net;
using System.Net.Http.Json;
using HabitTracker.Worker.Contracts;
using HabitTracker.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace HabitTracker.Worker.Clients;

public sealed class WorkerApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<WorkerApiClient> _logger;

    public WorkerApiClient(HttpClient http, IOptions<WorkerOptions> options, ILogger<WorkerApiClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.BaseAddress = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(options.Value.HttpTimeoutSeconds);
        var key = options.Value.SharedKey;
        if (!string.IsNullOrWhiteSpace(key))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Worker-Key", key);
    }

    public Task<SchedulePage<HabitScheduleSnapshot>> HabitPageAsync(string? after, int limit, CancellationToken ct) =>
        GetPageAsync<HabitScheduleSnapshot>($"internal/v1/habit-schedules?limit={limit}"
            + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}"), ct);

    public Task<SchedulePage<SummaryScheduleSnapshot>> SummaryPageAsync(string? after, int limit, CancellationToken ct) =>
        GetPageAsync<SummaryScheduleSnapshot>($"internal/v1/summary-schedules?limit={limit}"
            + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}"), ct);

    public async Task<ReminderEligibility> ReminderAsync(string userId, string habitId, string localDate, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"internal/v1/users/{Uri.EscapeDataString(userId)}/habits/{Uri.EscapeDataString(habitId)}/reminder?date={localDate}", ct);
        return await ReadAsync<ReminderEligibility>(response, ct);
    }

    public async Task<SummaryEligibility> SummaryAsync(string userId, string localDate, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"internal/v1/users/{Uri.EscapeDataString(userId)}/summary?date={localDate}", ct);
        return await ReadAsync<SummaryEligibility>(response, ct);
    }

    private async Task<SchedulePage<T>> GetPageAsync<T>(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        return await ReadAsync<SchedulePage<T>>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new WorkerApiUnauthorizedException();
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new WorkerApiNotFoundException();
        if (!response.IsSuccessStatusCode)
            throw new WorkerApiTransientException($"HTTP {(int)response.StatusCode}");

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new WorkerApiContractException("empty body");
    }
}

public sealed class WorkerApiUnauthorizedException : Exception;
public sealed class WorkerApiNotFoundException : Exception;
public sealed class WorkerApiTransientException(string message) : Exception(message);
public sealed class WorkerApiContractException(string message) : Exception(message);