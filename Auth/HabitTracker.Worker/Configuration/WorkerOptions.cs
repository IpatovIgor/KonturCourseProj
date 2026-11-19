namespace HabitTracker.Worker.Configuration;

public sealed class WorkerOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string SharedKey { get; set; } = "";
    
    public const string SectionName = "Worker";

    public int PollIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 100;
    public int LeaseSeconds { get; set; } = 120;
    public int LeaseRenewalSeconds { get; set; } = 30;
    public int HttpTimeoutSeconds { get; set; } = 10;
    public int BootstrapRetrySeconds { get; set; } = 5;
    public bool ReloadSchedulesOnStartup { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) throw new ArgumentException("WorkerApi BaseUrl is required", nameof(BaseUrl));
        if (PollIntervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(PollIntervalSeconds));
        if (BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(BatchSize));
        if (LeaseSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(LeaseSeconds));
        if (LeaseRenewalSeconds <= 0 || LeaseRenewalSeconds >= LeaseSeconds)
            throw new ArgumentOutOfRangeException(nameof(LeaseRenewalSeconds), "must be > 0 and < LeaseSeconds");
        if (HttpTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(HttpTimeoutSeconds));
        if (BootstrapRetrySeconds <= 0) throw new ArgumentOutOfRangeException(nameof(BootstrapRetrySeconds));
    }
}