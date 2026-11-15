using System.Net;
using System.Net.Http.Json;
using BackEnd.Data;
using HabitTracker.Messaging;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BackEnd.Tests.Controllers;

public sealed partial class HabitsHttpTests
{
    [MongoFact]
    public async Task WorkerPages_RequireKeyAndIncludeDisabledAndArchivedSchedules()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        const string route = "/internal/v1/habit-schedules";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Worker-Key", "incorrect");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Worker-Key");
        client.DefaultRequestHeaders.Add("X-Worker-Key", "worker-test-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?limit=201")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?after=")).StatusCode);

        await LoginAsync(client, "schedule-page@example.com");
        var firstId = (await CreateHabitAsync(client)).Split('/')[^1];
        var second = await CreateHabitAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(second)).StatusCode);
        var first = await ReadAsync(await client.GetAsync(route + "?limit=1"));
        var snapshot = Assert.Single(first["items"]!.AsArray())!;
        Assert.Equal(firstId, snapshot["habitId"]!.GetValue<string>());
        Assert.Equal(firstId, first["nextCursor"]!.GetValue<string>());
        Assert.Equal("Asia/Yekaterinburg", snapshot["timeZoneId"]!.GetValue<string>());
        Assert.False(snapshot["reminderEnabled"]!.GetValue<bool>());
        Assert.Equal("20:00", snapshot["reminderLocalTime"]!.GetValue<string>());
        Assert.False(snapshot.AsObject().ContainsKey("eventId"));

        var rest = await ReadAsync(await client.GetAsync(route + "?limit=1&after=" + firstId));
        Assert.Null(rest["nextCursor"]);
        Assert.Equal("archived", Assert.Single(rest["items"]!.AsArray())!["status"]!.GetValue<string>());

        await LoginAsync(client, "second-schedule@example.com");
        const string summaries = "/internal/v1/summary-schedules?limit=1";
        var summaryPage = await ReadAsync(await client.GetAsync(summaries));
        var summary = Assert.Single(summaryPage["items"]!.AsArray())!;
        Assert.Equal(summary["userId"]!.GetValue<string>(), summaryPage["nextCursor"]!.GetValue<string>());
        Assert.Equal(0L, summary["revision"]!.GetValue<long>());
        Assert.Equal("21:00", summary["localTime"]!.GetValue<string>());
        var last = await ReadAsync(await client.GetAsync(summaries + "&after=" + summaryPage["nextCursor"]!.GetValue<string>()));
        Assert.Single(last["items"]!.AsArray());
        Assert.Null(last["nextCursor"]);
    }

    [MongoFact]
    public async Task WorkerEligibility_UsesLocalCalendarAndCurrentState()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Worker-Key", "worker-test-key");
        await LoginAsync(client, "eligibility@example.com");
        var summaryPage = await ReadAsync(await client.GetAsync("/internal/v1/summary-schedules"));
        var userId = Assert.Single(summaryPage["items"]!.AsArray())!["userId"]!.GetValue<string>();
        var prefix = "/internal/v1/users/" + userId;
        const string today = "?date=2026-11-02";
        var settings = "/api/profile/summary-settings";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(settings, new { enabled = true, localTime = "00:00" })).StatusCode);
        var summary = await ReadAsync(await client.GetAsync(prefix + "/summary" + today));
        Assert.True(summary["eligible"]!.GetValue<bool>());
        Assert.Equal(0, summary["planned"]!.GetValue<int>());

        var habit = await CreateHabitAsync(client);
        var rawId = habit.Split('/')[^1];
        var check = prefix + "/habits/" + rawId + "/reminder";
        Assert.False((await ReadAsync(await client.GetAsync(check + today)))["eligible"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync(habit + "/reminder", new { enabled = true, localTime = "01:00" })).StatusCode);
        var early = await ReadAsync(await client.GetAsync(check + today));
        Assert.Equal("notDueYet", early["reason"]?.GetValue<string>());
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T20:00:00Z"), early["nextDueAtUtc"]!.GetValue<DateTimeOffset>());
        app.Clock.Now = app.Clock.Now.AddMinutes(30);
        Assert.True((await ReadAsync(await client.GetAsync(check + today)))["eligible"]!.GetValue<bool>());
        var yesterday = await ReadAsync(await client.GetAsync(check + "?date=2026-11-01"));
        Assert.False(yesterday["eligible"]!.GetValue<bool>());
        Assert.False(yesterday.ContainsKey("reason"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(habit + "/completions/2026-11-02", null)).StatusCode);
        var marked = await ReadAsync(await client.GetAsync(check + today));
        Assert.False(marked["eligible"]!.GetValue<bool>());
        Assert.Equal(2L, marked["revision"]!.GetValue<long>());
        summary = await ReadAsync(await client.GetAsync(prefix + "/summary" + today));
        Assert.Equal(1, summary["planned"]!.GetValue<int>());
        Assert.Equal(1, summary["completed"]!.GetValue<int>());
        Assert.Equal(0, summary["pending"]!.GetValue<int>());

        app.Clock.Now = app.Clock.Now.AddDays(1);
        var unscheduled = await ReadAsync(await client.GetAsync(check + "?date=2026-11-03"));
        Assert.False(unscheduled["eligible"]!.GetValue<bool>());
        Assert.False(unscheduled.ContainsKey("reason"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(habit)).StatusCode);
        app.Clock.Now = app.Clock.Now.AddDays(1);
        var archived = await ReadAsync(await client.GetAsync(check + "?date=2026-11-04"));
        Assert.False(archived["eligible"]!.GetValue<bool>());
        Assert.Equal(3L, archived["revision"]!.GetValue<long>());
        var missing = prefix + "/habits/" + ObjectId.GenerateNewId() + "/reminder";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(missing + today)).StatusCode);
    }

    [MongoFact]
    public async Task WorkerReminder_WhenDueTimeMovesLater_ReturnsNewDeadline()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Worker-Key", "worker-test-key");
        await LoginAsync(client, "rescheduled-reminder@example.com");
        var habit = await CreateHabitAsync(client);
        var page = await ReadAsync(await client.GetAsync("/internal/v1/habit-schedules"));
        var snapshot = Assert.Single(page["items"]!.AsArray())!;
        var check = $"/internal/v1/users/{snapshot["userId"]}/habits/{snapshot["habitId"]}/reminder?date=2026-11-02";
        await AssertSettingsAsync(await client.PutAsJsonAsync(habit + "/reminder",
            new { enabled = true, localTime = "00:00" }), true, "00:00");
        var initial = await ReadAsync(await client.GetAsync(check));
        Assert.True(initial["eligible"]!.GetValue<bool>());

        await AssertSettingsAsync(await client.PutAsJsonAsync(habit + "/reminder",
            new { enabled = true, localTime = "01:15" }), true, "01:15");
        var moved = await ReadAsync(await client.GetAsync(check));
        Assert.False(moved["eligible"]!.GetValue<bool>());
        Assert.Equal("notDueYet", moved["reason"]!.GetValue<string>());
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T20:15:00Z"), moved["nextDueAtUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(initial["revision"]!.GetValue<long>() + 1, moved["revision"]!.GetValue<long>());
    }

    [MongoFact]
    public async Task WorkerSchedules_UseNewCalendarOnEffectiveDateAndAwaitSummaryTime()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Worker-Key", "worker-test-key");
        await LoginAsync(client, "future-schedule@example.com");
        var habit = await CreateHabitAsync(client);
        var page = await ReadAsync(await client.GetAsync("/internal/v1/habit-schedules"));
        var snapshot = Assert.Single(page["items"]!.AsArray())!;
        var prefix = $"/internal/v1/users/{snapshot["userId"]}";
        var reminder = $"{prefix}/habits/{snapshot["habitId"]}/reminder";
        await AssertSettingsAsync(await client.PutAsJsonAsync(habit + "/reminder",
            new { enabled = true, localTime = "00:00" }), true, "00:00");
        await AssertSettingsAsync(await client.PutAsJsonAsync("/api/profile/summary-settings",
            new { enabled = true, localTime = "01:00" }), true, "01:00");

        using var edited = await client.PutAsJsonAsync(habit, new { name = "Зарядка", daysOfWeek = new[] { 2 } });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var monday = await ReadAsync(await client.GetAsync(reminder + "?date=2026-11-02"));
        Assert.True(monday["eligible"]!.GetValue<bool>());

        app.Clock.Now = app.Clock.Now.AddDays(1);
        const string tuesday = "?date=2026-11-03";
        var nextReminder = await ReadAsync(await client.GetAsync(reminder + tuesday));
        Assert.True(nextReminder["eligible"]!.GetValue<bool>());
        var earlySummary = await ReadAsync(await client.GetAsync(prefix + "/summary" + tuesday));
        Assert.False(earlySummary["eligible"]!.GetValue<bool>());
        Assert.Equal("notDueYet", earlySummary["reason"]!.GetValue<string>());
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T20:00:00Z"), earlySummary["nextDueAtUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(1, earlySummary["planned"]!.GetValue<int>());
        Assert.Equal(1, earlySummary["pending"]!.GetValue<int>());

        app.Clock.Now = app.Clock.Now.AddMinutes(30);
        var dueSummary = await ReadAsync(await client.GetAsync(prefix + "/summary" + tuesday));
        Assert.True(dueSummary["eligible"]!.GetValue<bool>());
    }

    [MongoFact]
    public async Task MongoIndexes_CanBeInitializedAgainWithTheSameKeys()
    {
        using var app = new HabitsApplication();
        using var client = app.CreateClient();
        var services = app.Services;
        await new MongoIndexInitializer(services.GetRequiredService<UserRepository>(),
            services.GetRequiredService<HabitRepository>(), services.GetRequiredService<MongoOutbox>())
            .StartAsync(CancellationToken.None);
        var database = services.GetRequiredService<IMongoDatabase>();
        await AssertIndex("users", new BsonDocument("email", 1), true);
        await AssertIndex("habits", new BsonDocument { { "userId", 1 }, { "status", 1 }, { "createdAt", -1 }, { "_id", -1 } });
        await AssertIndex("habitCompletions", new BsonDocument { { "userId", 1 }, { "habitId", 1 }, { "date", 1 } }, true);
        await AssertIndex("habitCompletions", new BsonDocument { { "userId", 1 }, { "date", 1 } });
        await AssertIndex("outboxEvents", new BsonDocument { { "status", 1 }, { "nextAttemptAt", 1 }, { "_id", 1 } });
        await AssertIndex("outboxEvents", new BsonDocument { { "status", 1 }, { "leaseUntil", 1 }, { "_id", 1 } });
        await AssertIndex("outboxEvents", new BsonDocument { { "status", 1 }, { "createdAt", 1 } });
        var ttl = await AssertIndex("outboxEvents", new BsonDocument("publishedAt", 1));
        Assert.Equal(30 * 86400L, ttl["expireAfterSeconds"].ToInt64());
        Assert.Equal(new BsonDocument("status", "published"), ttl["partialFilterExpression"].AsBsonDocument);

        async Task<BsonDocument> AssertIndex(string collection, BsonDocument keys, bool unique = false)
        {
            using var cursor = await database.GetCollection<BsonDocument>(collection).Indexes.ListAsync();
            var items = await cursor.ToListAsync();
            var index = Assert.Single(items, item => item["key"].AsBsonDocument.Equals(keys));
            Assert.Equal(unique, index.GetValue("unique", false).AsBoolean);
            return index;
        }
    }
}
