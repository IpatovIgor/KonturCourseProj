using System.Diagnostics.Metrics;
using HabitTracker.Contracts;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HabitTracker.Messaging;

public sealed class OutboxPublisher(MongoOutbox outbox, IBus bus, TimeProvider clock, ILogger<OutboxPublisher> logger)
    : BackgroundService
{
    public const string MeterName = "HabitTracker.Outbox";
    private readonly Meter _meter = new(MeterName);
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private long _oldestTicks;

    public override void Dispose()
    {
        base.Dispose();
        _meter.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.CreateObservableGauge("outbox.oldest.unpublished.age", OldestAge, "s");
        long? lastSample = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (lastSample is null || clock.GetElapsedTime(lastSample.Value) >= TimeSpan.FromSeconds(5))
                {
                    var oldest = await outbox.OldestUnpublishedAsync(stoppingToken);
                    Interlocked.Exchange(ref _oldestTicks, oldest?.Ticks ?? 0);
                    lastSample = clock.GetTimestamp();
                }

                if (await DispatchOneAsync(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Outbox polling failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
        }
    }

    private double OldestAge()
    {
        var ticks = Interlocked.Read(ref _oldestTicks);
        return ticks == 0 ? 0 : Math.Max(0, (clock.GetUtcNow().UtcTicks - ticks) / (double)TimeSpan.TicksPerSecond);
    }

    private async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        var item = await outbox.ClaimAsync(_owner, clock.GetUtcNow().UtcDateTime, ct);
        if (item is null)
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await PublishAsync(item, timeout.Token);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(error, "Publication failed for {EventId} ({Contract})", item.EventId, item.Contract);
            await outbox.RetryAsync(item, clock.GetUtcNow().UtcDateTime, ct);
            return true;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await outbox.PublishedAsync(item, now, ct);
        return true;
    }

    private Task PublishAsync(OutboxEvent item, CancellationToken ct) => item.Contract switch
    {
        nameof(HabitChangedV1) => PublishAsync<HabitChangedV1>(item, ct),
        nameof(SummarySettingsChangedV1) => PublishAsync<SummarySettingsChangedV1>(item, ct),
        nameof(NotificationReadyV1) => PublishAsync<NotificationReadyV1>(item, ct),
        _ => throw new InvalidOperationException($"Unrecognized outbox contract: {item.Contract}")
    };

    private Task PublishAsync<T>(OutboxEvent item, CancellationToken ct) where T : class, IVersionedEvent =>
        bus.Publish(item.Read<T>(), context =>
        {
            context.Mandatory = true;
            item.Context.Apply(context, item.EventId);
        }, ct);
}
