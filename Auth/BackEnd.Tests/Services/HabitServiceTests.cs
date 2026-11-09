using BackEnd.Data;
using BackEnd.Models;
using BackEnd.Services;
using FluentAssertions;
using MongoDB.Bson;
using Moq;
using Xunit;

namespace BackEnd.Tests.Services;

public class HabitServiceTests
{
    private static readonly ObjectId OwnerId = ObjectId.Parse("65aa00000000000000000001");
    private static readonly ObjectId HabitId = ObjectId.Parse("65aa00000000000000000011");
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly User _owner = new() { Id = OwnerId.ToString() };
    private readonly Mock<IHabitRepository> _habitRepositoryMock = new(MockBehavior.Strict);
    private readonly Mock<IUserRepository> _userRepositoryMock = new(MockBehavior.Strict);
    private readonly TestClock _clock = new();
    private readonly HabitService _habitService;

    public HabitServiceTests()
    {
        _habitService = new HabitService(_habitRepositoryMock.Object, _userRepositoryMock.Object, _clock);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-id")]
    public async Task GetOwner_WithInvalidSubject_ShouldRejectRequest(string? subject)
    {
        Func<Task> action = () => _habitService.GetOwnerAsync(subject, Token);

        var error = await action.Should().ThrowAsync<HabitApiException>();
        error.Which.Status.Should().Be(401);
    }

    [Fact]
    public async Task GetOwner_WhenTokenAccountNoLongerExists_ShouldRejectRequest()
    {
        _userRepositoryMock.Setup(repo => repo.GetByIdAsync(OwnerId.ToString(), Token))
            .ReturnsAsync((User?)null);

        Func<Task> action = () => _habitService.GetOwnerAsync(OwnerId.ToString(), Token);

        var error = await action.Should().ThrowAsync<HabitApiException>();
        error.Which.Code.Should().Be("unauthenticated");
    }

    [Fact]
    public async Task GetOwner_WithExistingAccount_ShouldReturnAccount()
    {
        _userRepositoryMock.Setup(repo => repo.GetByIdAsync(OwnerId.ToString(), Token))
            .ReturnsAsync(_owner);

        var result = await _habitService.GetOwnerAsync(OwnerId.ToString(), Token);

        result.Should().BeSameAs(_owner);
    }

    public static TheoryData<string?, string?, int[]?> InvalidHabitFields => new()
    {
        { null, null, [1] },
        { "   ", null, [1] },
        { new string('a', 101), null, [1] },
        { "Чтение", new string('a', 1001), [1] },
        { "Чтение", null, null },
        { "Чтение", null, [] },
        { "Чтение", null, [1, 1] },
        { "Чтение", null, [8] }
    };

    [Theory]
    [MemberData(nameof(InvalidHabitFields))]
    public async Task Create_WithInvalidFields_ShouldRejectHabit(string? name, string? description, int[]? days)
    {
        var request = new HabitRequest(name, description, days);

        Func<Task> action = () => _habitService.CreateAsync(_owner, request, Token);

        var error = await action.Should().ThrowAsync<HabitApiException>();
        error.Which.Code.Should().Be("validation_error");
    }

    [Fact]
    public async Task Create_JustAfterLocalMidnight_ShouldStartOnLocalDate()
    {
        _habitRepositoryMock.Setup(repo => repo.CreateAsync(OwnerId, It.IsAny<Func<User?, Habit>>(), Token))
            .ReturnsAsync((ObjectId _, Func<User?, Habit> build, CancellationToken _) => build(_owner));
        var request = new HabitRequest("  Чтение  ", "  ", [5, 1, 3]);

        var result = await _habitService.CreateAsync(_owner, request, Token);

        result.StartDate.Should().Be("2026-11-02");
        result.Name.Should().Be("Чтение");
        result.Description.Should().BeNull();
        result.DaysOfWeek.Should().Equal(1, 3, 5);
        result.NextSchedule.Should().BeNull();
    }

    [Theory]
    [InlineData("deleted", 1, 20)]
    [InlineData("active", 0, 20)]
    [InlineData("active", 1, 0)]
    [InlineData("archived", 1, 101)]
    public async Task List_WithInvalidPaginationOrStatus_ShouldRejectRequest(string status, int page, int pageSize)
    {
        Func<Task> action = () => _habitService.ListAsync(_owner, status, page, pageSize, Token);

        var error = await action.Should().ThrowAsync<HabitApiException>();
        error.Which.Code.Should().Be("validation_error");
    }

    [Fact]
    public async Task List_WithArchivedFilter_ShouldReturnTheRequestedPage()
    {
        var archivedHabit = MakeHabit();
        archivedHabit.Status = "archived";
        archivedHabit.ArchivedDate = "2026-11-02";
        _habitRepositoryMock.Setup(repo => repo.GetPageAsync(OwnerId, "archived", 2, 1, Token))
            .ReturnsAsync((new List<Habit> { archivedHabit }, 2L));

        var result = await _habitService.ListAsync(_owner, "archived", 2, 1, Token);

        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.Items.Should().ContainSingle().Which.Id.Should().Be(HabitId.ToString());
    }

    [Fact]
    public async Task Edit_TwiceOnTheSameDay_ShouldKeepOnlyTheLatestFutureSchedule()
    {
        var habit = MakeHabit();
        GivenHabit(habit);
        _habitRepositoryMock.Setup(repo => repo.TrySaveAsync(habit, Token)).ReturnsAsync(true);

        var first = await _habitService.EditAsync(_owner, HabitId, new("Чтение утром", null, [2]), Token);
        var second = await _habitService.EditAsync(_owner, HabitId, new("Чтение вечером", "После ужина", [4]), Token);

        first.NextSchedule.Should().BeEquivalentTo(new ScheduleResponse("2026-11-03", [2]));
        second.Name.Should().Be("Чтение вечером");
        second.Description.Should().Be("После ужина");
        second.DaysOfWeek.Should().Equal(1, 3, 5);
        second.NextSchedule.Should().BeEquivalentTo(new ScheduleResponse("2026-11-03", [4]));

        _clock.Now = _clock.Now.AddDays(1);
        var tomorrow = await _habitService.GetAsync(_owner, HabitId, Token);
        tomorrow.DaysOfWeek.Should().Equal(4);
        tomorrow.NextSchedule.Should().BeNull();
    }

    [Fact]
    public async Task Edit_WhenCurrentDaysAreResubmitted_ShouldCancelPendingSchedule()
    {
        var habit = MakeHabit();
        habit.ScheduleVersions.Add(new("2026-11-03", [2]));
        GivenHabit(habit);
        _habitRepositoryMock.Setup(repo => repo.TrySaveAsync(habit, Token)).ReturnsAsync(true);

        var result = await _habitService.EditAsync(_owner, HabitId, new("Чтение", null, [5, 3, 1]), Token);

        result.DaysOfWeek.Should().Equal(1, 3, 5);
        result.NextSchedule.Should().BeNull();
    }

    [Fact]
    public async Task Edit_WhenAnotherRequestArchivesHabit_ShouldReturnConflict()
    {
        var habit = MakeHabit();
        var archivedHabit = MakeHabit();
        archivedHabit.Status = "archived";
        archivedHabit.ArchivedDate = "2026-11-02";
        _habitRepositoryMock.SetupSequence(repo => repo.GetAsync(OwnerId, HabitId, Token))
            .ReturnsAsync(habit)
            .ReturnsAsync(archivedHabit);
        _habitRepositoryMock.Setup(repo => repo.TrySaveAsync(habit, Token)).ReturnsAsync(false);

        Func<Task> action = () => _habitService.EditAsync(_owner, HabitId, new("Чтение", null, [1]), Token);

        var error = await action.Should().ThrowAsync<HabitApiException>();
        error.Which.Code.Should().Be("habit_archived");
    }

    [Fact]
    public async Task Get_WithUnknownHabit_ShouldReturnNotFound()
    {
        _habitRepositoryMock.Setup(repo => repo.GetAsync(OwnerId, HabitId, Token)).ReturnsAsync((Habit?)null);

        Func<Task> action = () => _habitService.GetAsync(_owner, HabitId, Token);

        var error = await action.Should().ThrowAsync<HabitApiException>();
        error.Which.Status.Should().Be(404);
    }

    [Fact]
    public async Task Today_ShouldExcludeOtherWeekdaysAndShowCompletion()
    {
        var scheduled = MakeHabit();
        var unscheduled = MakeHabit();
        unscheduled.Id = ObjectId.GenerateNewId();
        unscheduled.ScheduleVersions = [new("2026-11-02", [2])];
        _habitRepositoryMock.Setup(repo => repo.GetActiveAsync(OwnerId, Token))
            .ReturnsAsync(new List<Habit> { unscheduled, scheduled });
        _habitRepositoryMock.Setup(repo => repo.GetCompletionsAsync(OwnerId, null, "2026-11-02", "2026-11-02", Token))
            .ReturnsAsync(new List<HabitCompletion> { Mark("2026-11-02") });

        var result = await _habitService.TodayAsync(_owner, Token);

        result.Date.Should().Be("2026-11-02");
        var item = result.Items.Should().ContainSingle().Which;
        item.Id.Should().Be(HabitId.ToString());
        item.Completed.Should().BeTrue();
    }

    [Theory]
    [InlineData("2026-10-26")]
    [InlineData("2026-11-04")]
    [InlineData("2026-11-03")]
    public async Task SetCompletion_WithDateOutsideScheduleOrLifetime_ShouldRejectMark(string date)
    {
        var habit = MakeHabit();
        _clock.Now = _clock.Now.AddDays(1);
        _habitRepositoryMock
            .Setup(repo => repo.SetCompletionAsync(OwnerId, HabitId, date, true, It.IsAny<Func<Habit?, DateTime>>(), Token))
            .Returns((ObjectId _, ObjectId _, string _, bool _, Func<Habit?, DateTime> validate, CancellationToken _) =>
            {
                validate(habit);
                return Task.CompletedTask;
            });

        Func<Task> action = () => _habitService.SetCompletionAsync(_owner, HabitId, date, true, Token);

        var error = await action.Should().ThrowAsync<HabitApiException>();
        error.Which.Code.Should().Be("invalid_completion_date");
    }

    [Theory]
    [InlineData("2026-11-01", null)]
    [InlineData("2026-11-02", "2026-11-01")]
    [InlineData("2026-11-02", "2026-11-03")]
    [InlineData("2025-11-01", "2026-11-02")]
    [InlineData("02.11.2026", "2026-11-02")]
    public async Task History_WithInvalidPeriod_ShouldRejectRequest(string? from, string? to)
    {
        Func<Task> action = () => _habitService.HistoryAsync(_owner, HabitId, from, to, Token);

        var error = await action.Should().ThrowAsync<HabitApiException>();
        error.Which.Code.Should().Be("validation_error");
    }

    [Fact]
    public async Task History_ShouldDistinguishCompletedMissedPendingAndUnscheduledDays()
    {
        _clock.Now = _clock.Now.AddDays(4);
        GivenHabit(MakeHabit());
        GivenMarks("2026-11-01", "2026-11-06", "2026-11-02");

        var result = await _habitService.HistoryAsync(_owner, HabitId, "2026-11-01", "2026-11-06", Token);

        result.Days.Should().Equal(
            new HabitDay("2026-11-02", "completed"),
            new HabitDay("2026-11-03", "notScheduled"),
            new HabitDay("2026-11-04", "missed"),
            new HabitDay("2026-11-05", "notScheduled"),
            new HabitDay("2026-11-06", "pending"));
    }

    [Fact]
    public async Task Statistics_WithMissedDay_ShouldBreakStreakAndUseOnlyRequestedDatesForRate()
    {
        _clock.Now = _clock.Now.AddDays(7);
        GivenHabit(MakeHabit());
        GivenMarks("2026-11-02", "2026-11-09", "2026-11-02", "2026-11-04");

        var result = await _habitService.StatisticsAsync(_owner, HabitId, "2026-11-04", "2026-11-09", Token);

        result.PlannedDays.Should().Be(3);
        result.CompletedDays.Should().Be(1);
        result.MissedDays.Should().Be(1);
        result.PendingDays.Should().Be(1);
        result.CompletionRatePercent.Should().Be(33.33m);
        result.CurrentStreak.Should().Be(0);
        result.LongestStreak.Should().Be(2);
    }

    [Fact]
    public async Task Statistics_WithoutScheduledDaysInPeriod_ShouldReturnNullRate()
    {
        _clock.Now = _clock.Now.AddDays(1);
        GivenHabit(MakeHabit());
        GivenMarks("2026-11-02", "2026-11-03", "2026-11-02");

        var result = await _habitService.StatisticsAsync(_owner, HabitId, "2026-11-03", "2026-11-03", Token);

        result.PlannedDays.Should().Be(0);
        result.CompletionRatePercent.Should().BeNull();
        result.CurrentStreak.Should().Be(1);
    }

    [Fact]
    public async Task Archive_ShouldStopFutureScheduleAndPreservePastCompletion()
    {
        var habit = MakeHabit();
        habit.ScheduleVersions.Add(new("2026-11-03", [2]));
        GivenHabit(habit);
        _habitRepositoryMock.Setup(repo => repo.TrySaveAsync(habit, Token)).ReturnsAsync(true);
        GivenMarks("2026-11-02", "2026-11-09", "2026-11-02");

        await _habitService.ArchiveAsync(_owner, HabitId, Token);
        _clock.Now = _clock.Now.AddDays(7);
        var archived = await _habitService.GetAsync(_owner, HabitId, Token);
        var statistics = await _habitService.StatisticsAsync(_owner, HabitId, "2026-11-02", "2026-11-09", Token);

        archived.ArchivedDate.Should().Be("2026-11-02");
        archived.DaysOfWeek.Should().Equal(1, 3, 5);
        archived.NextSchedule.Should().BeNull();
        statistics.PlannedDays.Should().Be(1);
        statistics.CompletedDays.Should().Be(1);
        statistics.CurrentStreak.Should().Be(0);
        statistics.LongestStreak.Should().Be(1);
    }

    private void GivenHabit(Habit habit) =>
        _habitRepositoryMock.Setup(repo => repo.GetAsync(OwnerId, HabitId, Token)).ReturnsAsync(habit);

    private void GivenMarks(string from, string to, params string[] dates) =>
        _habitRepositoryMock.Setup(repo => repo.GetCompletionsAsync(OwnerId, HabitId, from, to, Token))
            .ReturnsAsync(dates.Select(Mark).ToList());

    private static Habit MakeHabit() => new()
    {
        Id = HabitId,
        UserId = OwnerId,
        Name = "Чтение",
        StartDate = "2026-11-02",
        ScheduleVersions = [new("2026-11-02", [1, 3, 5])]
    };

    private static HabitCompletion Mark(string date) => new() { UserId = OwnerId, HabitId = HabitId, Date = date };

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 19, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
