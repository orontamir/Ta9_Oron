namespace Ta9.ProcessorService.Infrastructure.Events;

/// <summary>Exponential backoff with ±20% jitter so many processors don't reconnect in lockstep.</summary>
public static class ReconnectBackoff
{
    private const double Jitter = 0.2;

    public static TimeSpan Compute(int attempt, TimeSpan initial, TimeSpan max, Random? random = null)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var baseMs = Math.Min(max.TotalMilliseconds, initial.TotalMilliseconds * Math.Pow(2, exponent));
        var factor = 1 + ((random ?? Random.Shared).NextDouble() * 2 - 1) * Jitter;
        return TimeSpan.FromMilliseconds(baseMs * factor);
    }
}
