namespace Ta9.ProcessorService.Application;

/// <summary>
/// Decides when a progress event is worth sending: every <c>stepFraction</c> of the documents
/// (default 10%) or when <c>minInterval</c> (default 2s) has passed since the last report.
/// 100% is never reported here because the completion event already carries it.
/// Thread-safe: called concurrently from the parallel document loop.
/// </summary>
public sealed class ProgressThrottle
{
    private readonly object _gate = new();
    private readonly int _total;
    private readonly int _step;
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _time;
    private int _nextThreshold;
    private long _lastReportTimestamp;

    public ProgressThrottle(int total, TimeProvider time, double stepFraction = 0.1, TimeSpan? minInterval = null)
    {
        _total = total;
        _time = time;
        _step = Math.Max(1, (int)Math.Ceiling(total * stepFraction));
        _minInterval = minInterval ?? TimeSpan.FromSeconds(2);
        _nextThreshold = _step;
        _lastReportTimestamp = time.GetTimestamp();
    }

    public bool ShouldReport(int processed)
    {
        if (processed >= _total)
        {
            return false;
        }

        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var stepReached = processed >= _nextThreshold;
            var intervalElapsed = _time.GetElapsedTime(_lastReportTimestamp, now) >= _minInterval;
            if (!stepReached && !intervalElapsed)
            {
                return false;
            }

            _nextThreshold = (processed / _step + 1) * _step;
            _lastReportTimestamp = now;
            return true;
        }
    }
}
