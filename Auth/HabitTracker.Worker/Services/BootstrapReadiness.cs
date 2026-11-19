namespace HabitTracker.Worker.Services;

public sealed class BootstrapReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompleted;

    public void MarkReady() => _ready.TrySetResult();

    public Task WaitReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);
}