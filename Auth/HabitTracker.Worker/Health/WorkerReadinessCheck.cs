using HabitTracker.Worker.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HabitTracker.Worker.Health;

public sealed class WorkerReadinessCheck(BootstrapReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy("ready")
            : HealthCheckResult.Unhealthy("bootstrap not completed"));
}