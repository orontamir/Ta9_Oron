using System.Collections.Concurrent;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Domain;

namespace Ta9.ProcessorService.Tests.TestSupport;

/// <summary>Deterministic clock: time only moves when the test calls <see cref="Advance"/>.</summary>
public sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Analyzer whose behavior is supplied by the test (throw, block, cancel...).</summary>
public sealed class DelegateAnalyzer(Func<string, DocumentAnalysis> analyze) : IDocumentAnalyzer
{
    public static readonly DocumentAnalyzer Real = new();

    public DocumentAnalysis Analyze(string content) => analyze(content);
}

/// <summary>Wraps the real analyzer and records the maximum number of concurrent calls.</summary>
public sealed class ConcurrencyTrackingAnalyzer(TimeSpan delay) : IDocumentAnalyzer
{
    private readonly DocumentAnalyzer _inner = new();
    private int _current;
    private int _max;
    private int _calls;

    public int MaxConcurrency => Volatile.Read(ref _max);
    public int Calls => Volatile.Read(ref _calls);

    public DocumentAnalysis Analyze(string content)
    {
        var now = Interlocked.Increment(ref _current);
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref _max);
            if (now <= snapshot)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _max, now, snapshot) != snapshot);

        try
        {
            Thread.Sleep(delay);
            return _inner.Analyze(content);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
            Interlocked.Increment(ref _calls);
        }
    }
}

/// <summary>Thread-safe in-memory record of published lifecycle events.</summary>
public sealed class RecordingJobEventPublisher : IJobEventPublisher
{
    private readonly ConcurrentQueue<(string Type, Guid JobId)> _events = new();

    public Func<ProcessingJob, bool>? ThrowOnStarted { get; set; }

    public IReadOnlyList<string> TypesFor(Guid jobId) =>
        _events.Where(e => e.JobId == jobId).Select(e => e.Type).ToList();

    public void JobStarted(ProcessingJob job)
    {
        if (ThrowOnStarted?.Invoke(job) == true)
        {
            throw new InvalidOperationException("Publisher failure");
        }
        Record("JobStarted", job);
    }

    public void JobProgress(ProcessingJob job) => Record("JobProgress", job);
    public void JobCompleted(ProcessingJob job) => Record("JobCompleted", job);
    public void JobFailed(ProcessingJob job) => Record("JobFailed", job);
    public void JobCancelled(ProcessingJob job) => Record("JobCancelled", job);

    private void Record(string type, ProcessingJob job) => _events.Enqueue((type, job.JobId));
}

public static class TestData
{
    public static IReadOnlyList<ProcessDocumentRequest> Documents(params string[] contents) =>
        contents.Select((content, i) => new ProcessDocumentRequest(i, $"doc-{i}.txt", content)).ToList();

    public static IReadOnlyList<ProcessDocumentRequest> Documents(int count, string content = "lorem ipsum dolor") =>
        Enumerable.Range(0, count).Select(i => new ProcessDocumentRequest(i, $"doc-{i}.txt", content)).ToList();

    public static ProcessingJob Job(IReadOnlyList<ProcessDocumentRequest> documents, TimeProvider? time = null) =>
        new(Guid.NewGuid(), documents, correlationId: null, (time ?? TimeProvider.System).GetUtcNow());
}

public static class Eventually
{
    public static async Task<T> MatchAsync<T>(Func<Task<T>> probe, Func<T, bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var value = await probe();
            if (condition(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met in time. Last value: {value}");
            }

            await Task.Delay(25);
        }
    }

    public static Task TrueAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        MatchAsync(() => Task.FromResult(condition()), ok => ok, timeout);
}
