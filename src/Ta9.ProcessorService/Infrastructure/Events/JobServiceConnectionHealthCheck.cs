using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ta9.ProcessorService.Infrastructure.Events;

/// <summary>
/// Reports Degraded (not Unhealthy) while disconnected: the processor can still accept and process work,
/// events are buffered in the outbox until the connection comes back.
/// </summary>
public sealed class JobServiceConnectionHealthCheck(EventOutbox outbox) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object> { ["pendingEvents"] = outbox.PendingCount };
        return Task.FromResult(outbox.IsConnected
            ? HealthCheckResult.Healthy("Connected to Job Service", data)
            : HealthCheckResult.Degraded("Not connected to Job Service; events are buffered", data: data));
    }
}
