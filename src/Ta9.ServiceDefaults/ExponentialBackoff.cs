namespace Ta9.ServiceDefaults;

/// <summary>
/// Exponential backoff (initial × 2^(attempt-1), capped) with ±20% jitter, so many clients retrying the same
/// dependency do not hit it in lockstep. Used for WS reconnects and job dispatch retries.
/// </summary>
public static class ExponentialBackoff
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
