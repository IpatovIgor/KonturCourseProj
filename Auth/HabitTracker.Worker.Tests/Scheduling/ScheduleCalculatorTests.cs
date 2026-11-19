using HabitTracker.Worker.Models;
using HabitTracker.Worker.Scheduling;

namespace HabitTracker.Worker.Tests.Scheduling;

public sealed class ScheduleCalculatorTests
{
    private static readonly int[] EveryDay = { 1, 2, 3, 4, 5, 6, 7 }; // Monday=1 ... Sunday=7

    private static HabitSchedule Habit(string tz, string time, params int[] days) => new()
    {
        Id = "h1", UserId = "u1", Revision = 1, Status = "active", StartDate = "2026-01-01",
        ScheduleVersions = new[] { new ScheduleVersion { EffectiveFrom = "2026-01-01", DaysOfWeek = days } },
        TimeZoneId = tz, ReminderEnabled = true, ReminderLocalTime = time
    };

    [Fact]
    public void TomorrowScheduleDoesNotReplaceToday()
    {
        var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var habit = new HabitSchedule
        {
            Id = "h1", UserId = "u1", Status = "active", StartDate = "2026-11-01",
            ReminderEnabled = true, ReminderLocalTime = "20:00", TimeZoneId = "UTC",
            ScheduleVersions =
            [
                new() { EffectiveFrom = "2026-11-01", DaysOfWeek = [1] },
                new() { EffectiveFrom = "2026-11-17", DaysOfWeek = [2] }
            ]
        };
        var calculator = new ScheduleCalculator();
        Assert.Equal("2026-11-16", calculator.NextHabitDue(habit, now, null)?.LocalDate);
        Assert.Equal("2026-11-17", calculator.NextHabitDue(habit, now, "2026-11-16")?.LocalDate);
    }

    [Fact]
    public void SummaryUsesLocalDayAfterUtcMidnightBoundary()
    {
        var now = new DateTimeOffset(2026, 11, 16, 22, 0, 0, TimeSpan.Zero);
        var schedule = new SummarySchedule
        {
            Id = "u1", Enabled = true, LocalTime = "01:00", TimeZoneId = "Asia/Yekaterinburg"
        };
        var slot = new ScheduleCalculator().NextSummaryDue(schedule, now, "2026-11-15");
        Assert.Equal("2026-11-17", slot?.LocalDate);
        Assert.Equal(new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc), slot?.DueAtUtc);
    }

    [Fact]
    public void DueTimeUsesUserZone()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var calc = new ScheduleCalculator();
        var slot = calc.NextHabitDue(Habit("Asia/Yekaterinburg", "20:00", EveryDay), now, null);
        Assert.NotNull(slot);
        Assert.Equal("2026-11-15", slot!.LocalDate);
        Assert.Equal(new DateTime(2026, 11, 15, 15, 0, 0, DateTimeKind.Utc), slot.DueAtUtc); // 20:00 +05
    }

    [Fact]
    public void RestartCatchesUpOnlyToday()
    {
        // 22:00 UTC, срок 20:00 в той же зоне — только сегодняшний срок (в прошлом).
        var now = new DateTimeOffset(2026, 11, 15, 22, 0, 0, TimeSpan.Zero);
        var calc = new ScheduleCalculator();
        var slot = calc.NextHabitDue(Habit("UTC", "20:00", EveryDay), now, null);
        Assert.NotNull(slot);
        Assert.Equal("2026-11-15", slot!.LocalDate);
        Assert.True(slot.DueAtUtc < now.UtcDateTime);
    }

    [Fact]
    public void AfterTodaySkipsToTomorrow()
    {
        var now = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        var calc = new ScheduleCalculator();
        var slot = calc.NextHabitDue(Habit("UTC", "20:00", EveryDay), now, "2026-11-15");
        Assert.NotNull(slot);
        Assert.Equal("2026-11-16", slot!.LocalDate);
    }

    [Fact]
    public void DisabledReminderReturnsNull()
    {
        var now = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        var calc = new ScheduleCalculator();
        var habit = Habit("UTC", "20:00", EveryDay);
        var disabled = new HabitSchedule
        {
            Id = habit.Id, UserId = habit.UserId, Revision = habit.Revision, Status = habit.Status,
            StartDate = habit.StartDate, ScheduleVersions = habit.ScheduleVersions, TimeZoneId = habit.TimeZoneId,
            ReminderEnabled = false, ReminderLocalTime = habit.ReminderLocalTime
        };
        Assert.Null(calc.NextHabitDue(disabled, now, null));
    }

    [Fact]
    public void SpringGapUsesFirstValidMinute()
    {
        // Europe/Berlin, 2026-03-29 02:30 не существует -> 03:00 местного = 01:00 UTC.
        var now = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var calc = new ScheduleCalculator();
        var slot = calc.NextHabitDue(Habit("Europe/Berlin", "02:30", EveryDay), now, null);
        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), slot!.DueAtUtc);
    }

    [Fact]
    public void AutumnOverlapUsesFirstOccurrence()
    {
        // Europe/Berlin, 2026-10-25 02:30 повторяется -> первый UTC-момент = 00:30Z.
        var now = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var calc = new ScheduleCalculator();
        var slot = calc.NextHabitDue(Habit("Europe/Berlin", "02:30", EveryDay), now, null);
        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), slot!.DueAtUtc);
    }

    [Fact]
    public void WeekdaysOnlyUsesMondayOne()
    {
        // 2026-11-16 — понедельник. Привычка пн–пт (1..5).
        var monday = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        var calc = new ScheduleCalculator();
        var slot = calc.NextHabitDue(Habit("UTC", "20:00", 1, 2, 3, 4, 5), monday, null);
        Assert.NotNull(slot);
        Assert.Equal("2026-11-16", slot!.LocalDate);

        // 2026-11-15 — воскресенье. Пн–пт не подходит -> ближайший понедельник 16-го.
        var sunday = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        var sundaySlot = calc.NextHabitDue(Habit("UTC", "20:00", 1, 2, 3, 4, 5), sunday, null);
        Assert.NotNull(sundaySlot);
        Assert.Equal("2026-11-16", sundaySlot!.LocalDate);
    }
}