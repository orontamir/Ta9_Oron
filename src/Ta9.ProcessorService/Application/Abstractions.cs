using System.Diagnostics.CodeAnalysis;

namespace Ta9.ProcessorService.Application;

/// <summary>Holds jobs known to this Processor instance (queued, running and recently finished).</summary>
public interface IProcessingJobStore
{
    bool TryAdd(ProcessingJob job);
    bool TryGet(Guid jobId, [NotNullWhen(true)] out ProcessingJob? job);
    IReadOnlyCollection<ProcessingJob> GetAll();
    void Remove(Guid jobId);

    /// <summary>Removes finished jobs that completed before <paramref name="cutoff"/>. Returns how many were removed.</summary>
    int RemoveFinishedBefore(DateTimeOffset cutoff);
}

/// <summary>Bounded FIFO of jobs waiting for a worker.</summary>
public interface IJobQueue
{
    /// <summary>Non-blocking. Returns false when the queue is full (backpressure).</summary>
    bool TryEnqueue(ProcessingJob job);

    IAsyncEnumerable<ProcessingJob> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reports job lifecycle to the Job Service. Implementations must not block: they hand the event
/// to an outbox/transport and return immediately.
/// </summary>
public interface IJobEventPublisher
{
    void JobStarted(ProcessingJob job);
    void JobProgress(ProcessingJob job);
    void JobCompleted(ProcessingJob job);
    void JobFailed(ProcessingJob job);
    void JobCancelled(ProcessingJob job);
}
