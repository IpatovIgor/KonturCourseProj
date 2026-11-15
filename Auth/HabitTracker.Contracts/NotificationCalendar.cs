namespace HabitTracker.Contracts;

public static class NotificationCalendar
{
    public static DateOnly LocalDate(DateTimeOffset now, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime);

    public static DateTimeOffset DueAtUtc(DateOnly date, TimeOnly time, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = date.ToDateTime(time);

        // The contract has minute precision. Advance to the start of the first valid minute after a gap.
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
