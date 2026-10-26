using System.Collections.Concurrent;
using Ta9.Contracts.Processing;
using Ta9.JobService.Application;

namespace Ta9.JobService.Tests.TestSupport;

/// <summary>Scriptable stand-in for the Processor REST API.</summary>
public sealed class FakeProcessorClient : IProcessorClient
{
    private readonly ConcurrentQueue<(Guid JobId, IReadOnlyList<ProcessDocumentRequest> Documents)> _submitted = new();
    private readonly ConcurrentQueue<Guid> _cancelled = new();
    private readonly ConcurrentQueue<Guid> _lookups = new();

    public Func<Guid, Task<DispatchResult>> OnSubmit { get; set; } = _ => Task.FromResult(DispatchResult.Accepted());
    public Func<Guid, ProcessorLookup> OnGet { get; set; } = _ => new ProcessorLookup(LookupOutcome.NotFound);

    public IReadOnlyList<(Guid JobId, IReadOnlyList<ProcessDocumentRequest> Documents)> Submitted => _submitted.ToList();
    public IReadOnlyList<Guid> Cancelled => _cancelled.ToList();
    public IReadOnlyList<Guid> Lookups => _lookups.ToList();

    public async Task<DispatchResult> SubmitAsync(Guid jobId, IReadOnlyList<ProcessDocumentRequest> documents, CancellationToken cancellationToken)
    {
        _submitted.Enqueue((jobId, documents));
        return await OnSubmit(jobId);
    }

    public Task<ProcessorLookup> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        _lookups.Enqueue(jobId);
        return Task.FromResult(OnGet(jobId));
    }

    public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        _cancelled.Enqueue(jobId);
        return Task.FromResult(true);
    }
}

public sealed class RecordingDispatchSignal : IJobDispatchSignal
{
    private int _notifications;

    public int Notifications => Volatile.Read(ref _notifications);

    public void Notify() => Interlocked.Increment(ref _notifications);

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.Delay(timeout, cancellationToken);
}

/// <summary>Deterministic clock: time only moves when the test calls <see cref="Advance"/>.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _now += by;
}

public static class Eventually
{
    public static async Task TrueAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }
            await Task.Delay(25);
        }
    }
}
