namespace HabitTracker.Worker.Services;

public static class JobLease
{
    public const int MaxTransientHttpFailures = 5;

    public static TimeSpan Backoff(int httpFailureCount) => httpFailureCount switch
    {
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(4),
        4 => TimeSpan.FromMinutes(8),
        _ => TimeSpan.FromMinutes(8)
    };

    public static string NewOwner() => Guid.NewGuid().ToString("N");
}