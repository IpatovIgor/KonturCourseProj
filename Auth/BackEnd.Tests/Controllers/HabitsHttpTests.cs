using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BackEnd.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace BackEnd.Tests.Controllers;

public sealed class HabitsHttpTests
{
    [MongoFact]
    public async Task CreateAndEdit_ReturnTheSameTimestampsAsARead()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        await LoginAsync(client, "timestamps@example.com");
        app.Clock.Now = app.Clock.Now.AddTicks(12_345);

        using var create = await client.PostAsJsonAsync("/api/habits",
            new { name = "Чтение", daysOfWeek = new[] { 1 } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var path = Assert.IsType<Uri>(create.Headers.Location).OriginalString;
        var created = await ReadAsync(create);
        using var firstRead = await client.GetAsync(path);
        var stored = await ReadAsync(firstRead);
        Assert.Equal(stored["createdAt"]!.GetValue<string>(), created["createdAt"]!.GetValue<string>());
        Assert.Equal(stored["updatedAt"]!.GetValue<string>(), created["updatedAt"]!.GetValue<string>());

        app.Clock.Now = app.Clock.Now.AddHours(1).AddTicks(4_567);
        using var edit = await client.PutAsJsonAsync(path,
            new { name = "Чтение утром", daysOfWeek = new[] { 1 } });
        var edited = await ReadAsync(edit);
        using var secondRead = await client.GetAsync(path);
        var updated = await ReadAsync(secondRead);
        Assert.Equal(updated["updatedAt"]!.GetValue<string>(), edited["updatedAt"]!.GetValue<string>());
        Assert.NotEqual(stored["updatedAt"]!.GetValue<string>(), updated["updatedAt"]!.GetValue<string>());
    }

    [MongoFact]
    public async Task List_ReturnsTheFilteredTotalEvenOnAnEmptyPage()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        await LoginAsync(client, "pagination@example.com");

        var older = await CreateHabitAsync(client);
        app.Clock.Now = app.Clock.Now.AddMinutes(1);
        await CreateHabitAsync(client);
        using var archive = await client.DeleteAsync(await CreateHabitAsync(client));
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        using var secondPage = await client.GetAsync("/api/habits?page=2&pageSize=1");
        var page = await ReadAsync(secondPage);
        Assert.Equal(2, page["totalCount"]!.GetValue<int>());
        var item = Assert.Single(page["items"]!.AsArray());
        Assert.Equal(older.Split('/')[^1], item!["id"]!.GetValue<string>());

        using var outsidePage = await client.GetAsync("/api/habits?page=3&pageSize=1");
        var outside = await ReadAsync(outsidePage);
        Assert.Equal(2, outside["totalCount"]!.GetValue<int>());
        Assert.Empty(outside["items"]!.AsArray());
    }

    [MongoFact]
    public async Task Create_RejectsDaysOfWeekSentAsStrings()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        await LoginAsync(client, "validation@example.com");

        using var response = await client.PostAsJsonAsync("/api/habits",
            new { name = "Чтение", daysOfWeek = new[] { "1" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
    }

    [MongoFact]
    public async Task Habits_RequireJwt_AndHideOtherUsersHabits()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();

        using var anonymous = await client.GetAsync("/api/habits");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("unauthenticated", (await ReadAsync(anonymous))["code"]!.GetValue<string>());

        await LoginAsync(client, "first@example.com");
        var path = await CreateHabitAsync(client);
        await LoginAsync(client, "second@example.com");

        using var read = await client.GetAsync(path);
        using var update = await client.PutAsJsonAsync(path, new { name = "Чтение", daysOfWeek = new[] { 1 } });
        using var mark = await client.PutAsync(path + "/completions/2026-11-02", null);
        using var history = await client.GetAsync(path + "/history");
        using var archive = await client.DeleteAsync(path);
        Assert.All(new[] { read, update, mark, history, archive },
            response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [MongoFact]
    public async Task ScheduleChangesTomorrow_AndStatisticsFollowHistoricalMarks()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        await LoginAsync(client, "schedule@example.com");
        var path = await CreateHabitAsync(client);
        app.Clock.Now = new DateTimeOffset(2026, 11, 8, 19, 30, 0, TimeSpan.Zero);

        using var update = await client.PutAsJsonAsync(path, new { name = "Зарядка утром", daysOfWeek = new[] { 2, 4 } });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await ReadAsync(update);
        Assert.Equal("2026-11-10", updated["nextSchedule"]!["effectiveFrom"]!.GetValue<string>());

        using var todayResponse = await client.GetAsync("/api/habits/today");
        var today = await ReadAsync(todayResponse);
        Assert.Equal("2026-11-09", today["date"]!.GetValue<string>());
        Assert.False(Assert.Single(today["items"]!.AsArray())!["completed"]!.GetValue<bool>());

        var concurrentMarks = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PutAsync(path + "/completions/2026-11-04", null)));
        foreach (var response in concurrentMarks)
        {
            using (response)
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.Equal(1L, await app.CountCompletionsAsync());

        using var friday = await client.PutAsync(path + "/completions/2026-11-06", null);
        Assert.Equal(HttpStatusCode.NoContent, friday.StatusCode);
        using var unscheduled = await client.PutAsync(path + "/completions/2026-11-05", null);
        Assert.Equal(HttpStatusCode.BadRequest, unscheduled.StatusCode);

        using var historyResponse = await client.GetAsync(path + "/history?from=2026-11-04&to=2026-11-05");
        var days = (await ReadAsync(historyResponse))["days"]!.AsArray();
        Assert.Equal(new[] { "completed", "notScheduled" },
            days.Select(day => day!["status"]!.GetValue<string>()));

        using var statsResponse = await client.GetAsync(path + "/statistics?from=2026-11-02&to=2026-11-09");
        var stats = await ReadAsync(statsResponse);
        Assert.Equal(4, stats["plannedDays"]!.GetValue<int>());
        Assert.Equal(2, stats["completedDays"]!.GetValue<int>());
        Assert.Equal(2, stats["currentStreak"]!.GetValue<int>());
        Assert.Equal(50m, stats["completionRatePercent"]!.GetValue<decimal>());

        for (var i = 0; i < 2; i++)
        {
            using var cancel = await client.DeleteAsync(path + "/completions/2026-11-04");
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        using var correctedResponse = await client.GetAsync(path + "/statistics");
        var corrected = await ReadAsync(correctedResponse);
        Assert.Equal(1, corrected["longestStreak"]!.GetValue<int>());
        Assert.Equal(25m, corrected["completionRatePercent"]!.GetValue<decimal>());
    }

    [MongoFact]
    public async Task ArchivedHabit_LeavesActiveLists_ButKeepsHistory()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        await LoginAsync(client, "archive@example.com");
        var path = await CreateHabitAsync(client);
        using var mark = await client.PutAsync(path + "/completions/2026-11-02", null);
        Assert.Equal(HttpStatusCode.NoContent, mark.StatusCode);

        using var archive = await client.DeleteAsync(path);
        using var repeat = await client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);

        using var active = await client.GetAsync("/api/habits");
        using var archived = await client.GetAsync("/api/habits?status=archived");
        using var today = await client.GetAsync("/api/habits/today");
        Assert.Empty((await ReadAsync(active))["items"]!.AsArray());
        Assert.Single((await ReadAsync(archived))["items"]!.AsArray());
        Assert.Empty((await ReadAsync(today))["items"]!.AsArray());

        using var history = await client.GetAsync(path + "/history");
        Assert.Equal("completed", Assert.Single((await ReadAsync(history))["days"]!.AsArray())!["status"]!.GetValue<string>());
        using var cancel = await client.DeleteAsync(path + "/completions/2026-11-02");
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
    }

    private static async Task LoginAsync(HttpClient client, string email)
    {
        const string password = "test-password-only";
        using var register = await client.PostAsJsonAsync("/api/auth/register", new { nickName = "Test", email, password });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        var token = (await ReadAsync(login))["token"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<string> CreateHabitAsync(HttpClient client)
    {
        using var create = await client.PostAsJsonAsync("/api/habits", new { name = "  Зарядка  ", daysOfWeek = new[] { 1, 3, 5 } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var habit = await ReadAsync(create);
        Assert.Equal("Зарядка", habit["name"]!.GetValue<string>());
        Assert.Equal("2026-11-02", habit["startDate"]!.GetValue<string>());
        return Assert.IsType<Uri>(create.Headers.Location).OriginalString;
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized, content);
        return JsonNode.Parse(content)!.AsObject();
    }

    private sealed class HabitsApplication : WebApplicationFactory<AuthController>
    {
        private readonly string _connectionString = Environment.GetEnvironmentVariable("KONTUR_TEST_MONGO_URI")!;
        private readonly string _database = "kontur_habits_tests_" + Guid.NewGuid().ToString("N");

        public MutableTimeProvider Clock { get; } = new();

        public Task<long> CountCompletionsAsync() => new MongoClient(_connectionString).GetDatabase(_database)
            .GetCollection<MongoDB.Bson.BsonDocument>("habitCompletions")
            .CountDocumentsAsync(Builders<MongoDB.Bson.BsonDocument>.Filter.Empty);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDbSettings:ConnectionString"] = _connectionString,
                ["MongoDbSettings:DatabaseName"] = _database,
                ["JwtSettings:Secret"] = "habits-test-signing-key-12345678901234567890",
                ["JwtSettings:Issuer"] = "habits-tests",
                ["JwtSettings:Audience"] = "habits-tests",
                ["JwtSettings:ExpiryMinutes"] = "60"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                new MongoClient(_connectionString).DropDatabase(_database);
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 19, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KONTUR_TEST_MONGO_URI")))
            Skip = "Set KONTUR_TEST_MONGO_URI to an isolated MongoDB replica set.";
    }
}
