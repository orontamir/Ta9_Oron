using System.ComponentModel.DataAnnotations;

namespace Ta9.ProcessorService.Infrastructure.Events;

public sealed class JobServiceEventsOptions
{
    public const string SectionName = "JobServiceEvents";

    /// <summary>WebSocket endpoint of the Job Service.</summary>
    [Required]
    public Uri Url { get; set; } = new("ws://localhost:5001/ws/processor-events");

    /// <summary>Max lifecycle events kept while waiting for an ack. Beyond this, events are dropped (reconciler recovers).</summary>
    [Range(1, 1_000_000)]
    public int OutboxCapacity { get; set; } = 10_000;

    /// <summary>A sent but unacknowledged event is resent after this delay, even without a reconnect.</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the sender wakes up to check for ack timeouts when there is no new event.</summary>
    public TimeSpan ResendCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
}
