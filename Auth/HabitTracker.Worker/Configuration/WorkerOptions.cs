namespace HabitTracker.Worker.Configuration;

public sealed class WorkerOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5160";
    public string SharedKey { get; set; } = "";
    
    public const string SectionName = "Worker";

    public int PollIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 100;
    public int LeaseSeconds { get; set; } = 120;
    public int LeaseRenewalSeconds { get; set; } = 30;
    public int HttpTimeoutSeconds { get; set; } = 10;
    public int BootstrapRetrySeconds { get; set; } = 5;
    public bool ReloadSchedulesOnStartup { get; set; }
    public bool DailySummaryEnabled { get; set; } = true;

    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(url.Host) || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("Configure a valid WorkerApi__BaseUrl.");
        if (string.IsNullOrWhiteSpace(SharedKey) || SharedKey.Any(c => c < 32 || c == 127))
            throw new ArgumentException("Configure WorkerApi__SharedKey.");
        if (PollIntervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(PollIntervalSeconds));
        if (BatchSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(BatchSize));
        if (LeaseSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(LeaseSeconds));
        if (LeaseRenewalSeconds <= 0 || LeaseRenewalSeconds >= LeaseSeconds)
            throw new ArgumentOutOfRangeException(nameof(LeaseRenewalSeconds), "must be > 0 and < LeaseSeconds");
        if (HttpTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(HttpTimeoutSeconds));
        if (BootstrapRetrySeconds <= 0) throw new ArgumentOutOfRangeException(nameof(BootstrapRetrySeconds));
    }
}
