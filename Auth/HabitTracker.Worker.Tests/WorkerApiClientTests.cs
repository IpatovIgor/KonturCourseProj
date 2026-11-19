using System.Net;
using System.Text;
using HabitTracker.Worker.Clients;
using HabitTracker.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace HabitTracker.Worker.Tests;

public sealed class WorkerApiClientTests
{
    [Theory]
    [InlineData(400, "{}")]
    [InlineData(200, "{")]
    [InlineData(200, "{\"eligible\":true}")]
    [InlineData(200, "{\"eligible\":false,\"planned\":0,\"completed\":0,\"pending\":0,\"revision\":0,\"reason\":\"notDueYet\"}")]
    public async Task InvalidResponsesAreContractFailures(int status, string body)
    {
        var api = Make((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));
        await Assert.ThrowsAsync<WorkerApiContractException>(() => api.SummaryAsync("user", "2026-11-19", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkErrorsAndTimeoutsAreTransient(bool timeout)
    {
        var api = Make(async (_, ct) =>
        {
            if (timeout)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new HttpRequestException("connection failed");
        });
        await Assert.ThrowsAsync<WorkerApiTransientException>(() => api.SummaryAsync("user", "2026-11-19", default));
    }

    [Fact]
    public async Task PagesSendKeyAndUseCursorOnlyAfterFirstRequest()
    {
        const string cursor = "507f1f77bcf86cd799439011";
        var requests = new List<string>();
        var api = Make((request, _) =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Worker-Key")));
            requests.Add(request.RequestUri!.PathAndQuery);
            var json = requests.Count == 1
                ? $"{{\"items\":[],\"nextCursor\":\"{cursor}\"}}"
                : "{\"items\":[],\"nextCursor\":null}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });
        var first = await api.HabitPageAsync(null, 100, default);
        Assert.Equal(cursor, first.NextCursor);
        var second = await api.HabitPageAsync(first.NextCursor, 100, default);
        Assert.Null(second.NextCursor);
        Assert.Equal(new[] { "/internal/v1/habit-schedules?limit=100",
            $"/internal/v1/habit-schedules?limit=100&after={cursor}" }, requests);
    }

    [Fact]
    public async Task CallerCancellationIsNotAnHttpFailure()
    {
        using var stop = new CancellationTokenSource();
        var api = Make(async (_, ct) =>
        {
            await stop.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.SummaryAsync("user", "2026-11-19", stop.Token));
    }

    [Fact]
    public async Task MissingCursorCannotBeMistakenForTheLastPage()
    {
        var api = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"items\":[]}", Encoding.UTF8, "application/json")
        }));
        await Assert.ThrowsAsync<WorkerApiContractException>(() => api.HabitPageAsync(null, 100, default));
    }

    private static WorkerApiClient Make(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new StubHandler(send)), Options.Create(new WorkerOptions { HttpTimeoutSeconds = 1, SharedKey = "test-key" }));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
