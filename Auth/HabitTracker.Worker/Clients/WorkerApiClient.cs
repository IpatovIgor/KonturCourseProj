using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HabitTracker.Worker.Contracts;
using HabitTracker.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace HabitTracker.Worker.Clients;

public sealed class WorkerApiClient
{
    private readonly HttpClient _http;

    public WorkerApiClient(HttpClient http, IOptions<WorkerOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(options.Value.HttpTimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(options.Value.SharedKey))
            _http.DefaultRequestHeaders.Add("X-Worker-Key", options.Value.SharedKey);
    }

    public Task<SchedulePage<HabitScheduleSnapshot>> HabitPageAsync(string? after, int limit, CancellationToken ct) =>
        GetPageAsync<HabitScheduleSnapshot>("habit-schedules", after, limit, ct);

    public Task<SchedulePage<SummaryScheduleSnapshot>> SummaryPageAsync(string? after, int limit, CancellationToken ct) =>
        GetPageAsync<SummaryScheduleSnapshot>("summary-schedules", after, limit, ct);

    private async Task<SchedulePage<T>> GetPageAsync<T>(string resource, string? after, int limit, CancellationToken ct)
    {
        var page = await GetAsync<SchedulePage<T>>($"internal/v1/{resource}?limit={limit}"
            + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}"), ct);
        if (page.Items is null || page.Items.Any(item => item is null) ||
            (page.NextCursor is not null && (!MongoDB.Bson.ObjectId.TryParse(page.NextCursor, out _) ||
                string.CompareOrdinal(page.NextCursor, after) <= 0)))
            throw new WorkerApiContractException("invalid_page");
        return page;
    }

    public async Task<ReminderEligibility> ReminderAsync(string userId, string habitId, string localDate, CancellationToken ct)
    {
        var result = await GetAsync<ReminderEligibility>(
            $"internal/v1/users/{Uri.EscapeDataString(userId)}/habits/{Uri.EscapeDataString(habitId)}/reminder?date={localDate}", ct);
        ValidateEligibility(result.Eligible, result.Revision, result.Reason, result.NextDueAtUtc);
        if (result.HabitName is null || (result.Eligible && string.IsNullOrWhiteSpace(result.HabitName)))
            throw new WorkerApiContractException("invalid_habit_name");
        return result;
    }

    public async Task<SummaryEligibility> SummaryAsync(string userId, string localDate, CancellationToken ct)
    {
        var result = await GetAsync<SummaryEligibility>(
            $"internal/v1/users/{Uri.EscapeDataString(userId)}/summary?date={localDate}", ct);
        ValidateEligibility(result.Eligible, result.Revision, result.Reason, result.NextDueAtUtc);
        if (result.Completed < 0 || result.Pending < 0 || result.Planned != (long)result.Completed + result.Pending)
            throw new WorkerApiContractException("invalid_summary_counts");
        return result;
    }

    private static void ValidateEligibility(bool eligible, long revision, string? reason, DateTimeOffset? nextDueAt)
    {
        if (revision < 0 || (reason is null ? nextDueAt is not null :
            eligible || reason != "notDueYet" || nextDueAt is null))
            throw new WorkerApiContractException("invalid_eligibility");
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new WorkerApiUnauthorizedException();
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new WorkerApiNotFoundException();
            if (status == 429 || status is >= 500 and <= 599)
                throw new WorkerApiTransientException($"http_{status}");
            if (status != 200)
                throw new WorkerApiContractException($"http_{status}");

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                ?? throw new WorkerApiContractException("empty_body");
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            throw new WorkerApiTransientException("http_network");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkerApiTransientException("http_timeout");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new WorkerApiContractException("invalid_json");
        }
    }
}

public sealed class WorkerApiUnauthorizedException : Exception;
public sealed class WorkerApiNotFoundException : Exception;
public sealed class WorkerApiTransientException(string message) : Exception(message);
public sealed class WorkerApiContractException(string message) : Exception(message);
