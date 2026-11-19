using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BackEnd.Controllers;
using HabitTracker.Contracts;
using HabitTracker.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BackEnd.Tests.Controllers;

public sealed partial class HabitsHttpTests
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

    [MongoFact]
    public async Task NotificationSettings_RequireJwtAndPreserveDefaultsAndOwnership()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        const string summary = "/api/profile/summary-settings";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(summary)).StatusCode);
        await LoginAsync(client, "settings-owner@example.com");
        var habit = await CreateHabitAsync(client);
        var reminder = habit + "/reminder";
        var db = app.Services.GetRequiredService<IMongoDatabase>();
        await db.GetCollection<BsonDocument>("habits").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Unset("reminderEnabled").Unset("reminderLocalTime"));
        await db.GetCollection<BsonDocument>("users").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Unset("summaryEnabled").Unset("summaryLocalTime").Unset("summaryRevision"));

        await AssertSettingsAsync(await client.GetAsync(reminder), false, "20:00");
        await AssertSettingsAsync(await client.GetAsync(summary), false, "21:00");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync(reminder, new { enabled = true, localTime = "24:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync(summary, new { localTime = "07:00" })).StatusCode);
        await AssertSettingsAsync(await client.PutAsJsonAsync(reminder, new { enabled = true, localTime = "00:00" }), true, "00:00");
        await AssertSettingsAsync(await client.PutAsJsonAsync(reminder, new { enabled = true, localTime = "00:00" }), true, "00:00");
        await AssertSettingsAsync(await client.PutAsJsonAsync(summary, new { enabled = true, localTime = "23:59" }), true, "23:59");

        using var relogin = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "settings-owner@example.com", password = "test-password-only" });
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        await AssertSettingsAsync(await client.GetAsync(summary), true, "23:59");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(habit)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync(reminder, new { enabled = false, localTime = "20:00" })).StatusCode);
        using var other = app.CreateClient();
        await LoginAsync(other, "settings-other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(reminder)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PutAsJsonAsync(reminder, new { enabled = true, localTime = "20:00" })).StatusCode);
    }

    [MongoFact]
    public async Task Outbox_RecordsOnlyChangesAndRollsBackWhenEventCannotBeInserted()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        await LoginAsync(client, "outbox@example.com");
        var path = await CreateHabitAsync(client);
        var completion = path + "/completions/2026-11-02";
        var edit = new { name = "Новое название", daysOfWeek = new[] { 1, 3, 5 } };
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, edit)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path + "/reminder",
                new { enabled = true, localTime = "12:00" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/profile/summary-settings",
                new { enabled = true, localTime = "21:00" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(completion, null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(completion)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(completion)).StatusCode);
        var database = app.Services.GetRequiredService<IMongoDatabase>();
        var events = database.GetCollection<BsonDocument>("outboxEvents");
        var payloads = (await events.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync())
            .Select(item => item["payload"].AsBsonDocument).ToArray();
        Assert.Equal(new[] { "created", "edited", "reminderChanged", "completionChanged", "completionChanged" },
            payloads.Where(payload => payload.Contains("change")).OrderBy(payload => payload["revision"].ToInt64())
                .Select(payload => payload["change"].AsString));
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, payloads.Where(payload => payload.Contains("change"))
            .Select(payload => payload["revision"].ToInt64()).Order());
        Assert.Equal(1, Assert.Single(payloads.Where(payload => !payload.Contains("change")))["revision"].ToInt64());

        await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "outboxEvents" },
            { "validator", new BsonDocument("contract", "never-a-contract") }
        });
        using var failed = await client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var problem = JsonNode.Parse(await failed.Content.ReadAsStringAsync())!.AsObject();
        Assert.False(string.IsNullOrEmpty(problem["traceId"]?.GetValue<string>()));
        Assert.False(problem.ContainsKey("detail"));
        Assert.Null((await ReadAsync(await client.GetAsync(path)))["archivedAt"]);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "outboxEvents" }, { "validator", new BsonDocument() }
        });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
        var archived = await events.Find(Builders<BsonDocument>.Filter.Eq("payload.change", "archived")).SingleAsync();
        Assert.Equal(5, archived["payload"]["revision"].ToInt64());
        Assert.Equal("archived", archived["payload"]["status"].AsString);
    }

    [MongoFact]
    public async Task DisabledNotifications_HideSettingsRoutes()
    {
        using var app = new HabitsApplication(notificationsEnabled: false);
        using var client = app.CreateClient();
        await LoginAsync(client, "notifications-disabled@example.com");
        var habit = await CreateHabitAsync(client);
        foreach (var path in new[] { habit + "/reminder", "/api/profile/summary-settings" })
        {
            using var get = await client.GetAsync(path);
            using var put = await client.PutAsJsonAsync(path, new { enabled = true, localTime = "12:00" });
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        }
    }

    [MongoFact]
    public async Task DisabledNotifications_HabitWritesKeepExistingOutboxWithoutAppendingEvents()
    {
        using var app = new HabitsApplication(notificationsEnabled: false);
        using var client = app.CreateClient();
        await LoginAsync(client, "outbox-disabled@example.com");
        var database = app.Services.GetRequiredService<IMongoDatabase>();
        var events = database.GetCollection<BsonDocument>("outboxEvents");
        var correlation = Guid.NewGuid();
        var previousEvent = new SummarySettingsChangedV1(Guid.NewGuid(), app.Clock.GetUtcNow().UtcDateTime,
            correlation, ObjectId.GenerateNewId().ToString(), 1, true, "21:00", "Asia/Yekaterinburg");
        var existing = OutboxEvent.Create(previousEvent, new(correlation, null, null)).ToBsonDocument();
        await events.InsertOneAsync(existing);

        var path = await CreateHabitAsync(client);
        using var edited = await client.PutAsJsonAsync(path, new { name = "Чтение", daysOfWeek = new[] { 1, 3, 5 } });
        Assert.Equal("Чтение", (await ReadAsync(edited))["name"]!.GetValue<string>());
        var completion = path + "/completions/2026-11-02";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(completion, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(completion)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);

        Assert.Equal(existing, Assert.Single(await events.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()));
    }

    private static async Task AssertSettingsAsync(HttpResponseMessage response, bool enabled, string time)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(enabled, body["enabled"]!.GetValue<bool>());
            Assert.Equal(time, body["localTime"]!.GetValue<string>());
        }
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

    private sealed class HabitsApplication(bool useRabbit = false, bool notificationsEnabled = true, bool dailySummaryEnabled = true)
        : WebApplicationFactory<AuthController>
    {
        private readonly string _connectionString = Environment.GetEnvironmentVariable("KONTUR_TEST_MONGO_URI")!;
        private readonly string _database = "kontur_habits_tests_" + Guid.NewGuid().ToString("N");

        public MutableTimeProvider Clock { get; } = new(useRabbit);

        public Task<long> CountCompletionsAsync() => new MongoClient(_connectionString).GetDatabase(_database)
            .GetCollection<MongoDB.Bson.BsonDocument>("habitCompletions")
            .CountDocumentsAsync(Builders<MongoDB.Bson.BsonDocument>.Filter.Empty);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (useRabbit)
            {
                builder.UseSetting("RabbitMq:Host", Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_URI"));
                builder.UseSetting("RabbitMq:Username", Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_USER"));
                builder.UseSetting("RabbitMq:Password", Environment.GetEnvironmentVariable("KONTUR_TEST_RABBIT_PASSWORD"));
            }
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDbSettings:ConnectionString"] = _connectionString,
                ["MongoDbSettings:DatabaseName"] = _database,
                ["JwtSettings:Secret"] = "habits-test-signing-key-12345678901234567890",
                ["JwtSettings:Issuer"] = "habits-tests",
                ["JwtSettings:Audience"] = "habits-tests",
                ["JwtSettings:ExpiryMinutes"] = "60",
                ["WorkerApi:SharedKey"] = "worker-test-key",
                ["Features:NotificationsEnabled"] = notificationsEnabled.ToString(),
                ["Features:DailySummaryEnabled"] = dailySummaryEnabled.ToString()
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

    private sealed class MutableTimeProvider(bool advance) : TimeProvider
    {
        private readonly long _startedAt = System.GetTimestamp();
        private long _ticks = advance ? System.GetUtcNow().UtcTicks
            : new DateTimeOffset(2026, 11, 1, 19, 30, 0, TimeSpan.Zero).UtcTicks;

        private long ElapsedTicks => advance ? System.GetElapsedTime(_startedAt).Ticks : 0;

        public DateTimeOffset Now
        {
            get => new(Interlocked.Read(ref _ticks) + ElapsedTicks, TimeSpan.Zero);
            set => Interlocked.Exchange(ref _ticks, value.UtcTicks - ElapsedTicks);
        }

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
