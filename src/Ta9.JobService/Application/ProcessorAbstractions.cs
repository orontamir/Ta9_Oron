using System.Threading.Channels;
using Ta9.Contracts.Processing;

namespace Ta9.JobService.Application;

public enum DispatchOutcome
{
    /// <summary>The Processor accepted the job (202), or already had it (200).</summary>
    Accepted,

    /// <summary>The Processor is at capacity (503). Backpressure, not a failure: retry after the hint.</summary>
    Busy,

    /// <summary>The Processor refused the payload (4xx). Retrying cannot help.</summary>
    Rejected,

    /// <summary>Network error, timeout or 5xx. Retry with backoff.</summary>
    Unreachable
}

public sealed record DispatchResult(DispatchOutcome Outcome, TimeSpan? RetryAfter = null, string? Error = null)
{
    public static DispatchResult Accepted() => new(DispatchOutcome.Accepted);
    public static DispatchResult Busy(TimeSpan retryAfter) => new(DispatchOutcome.Busy, retryAfter);
    public static DispatchResult Rejected(string error) => new(DispatchOutcome.Rejected, Error: error);
    public static DispatchResult Unreachable(string error) => new(DispatchOutcome.Unreachable, Error: error);
}

public enum LookupOutcome
{
    Found,
    NotFound,
    Unreachable
}

public sealed record ProcessorLookup(LookupOutcome Outcome, ProcessJobStatusResponse? Status = null, string? Error = null);

/// <summary>Job Service's view of the Processor REST API. Implementations translate transport errors into outcomes.</summary>
public interface IProcessorClient
{
    Task<DispatchResult> SubmitAsync(Guid jobId, IReadOnlyList<ProcessDocumentRequest> documents, CancellationToken cancellationToken);

    Task<ProcessorLookup> GetAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Best effort: never throws, returns false if the Processor could not be told.</summary>
    Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken);
}

/// <summary>Wakes the dispatcher as soon as there is new work instead of waiting for its next poll.</summary>
public interface IJobDispatchSignal
{
    void Notify();

    /// <summary>Completes when notified or after <paramref name="timeout"/>, whichever comes first.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class JobDispatchSignal(TimeProvider time) : IJobDispatchSignal
{
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _signal.Writer.TryWrite(0);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _signal.Reader.WaitToReadAsync(cancellationToken).AsTask().WaitAsync(timeout, time, cancellationToken);
            _signal.Reader.TryRead(out _);
        }
        catch (TimeoutException)
        {
            // Regular poll tick.
        }
    }
}
