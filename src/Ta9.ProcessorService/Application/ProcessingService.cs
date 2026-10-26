using Ta9.Contracts.Processing;
using Ta9.ServiceDefaults;

namespace Ta9.ProcessorService.Application;

public enum SubmitOutcome
{
    Accepted,
    AlreadyExists,
    QueueFull
}

public sealed record SubmitResult(SubmitOutcome Outcome, ProcessingJob? Job);

public enum CancelOutcome
{
    Cancelled,
    NotFound,
    AlreadyFinished
}

/// <summary>Use-cases behind /api/process: submit (idempotent by job id), query and cancel.</summary>
public sealed class ProcessingService(
    IProcessingJobStore store,
    IJobQueue queue,
    IJobEventPublisher events,
    TimeProvider time,
    ILogger<ProcessingService> logger)
{
    public SubmitResult Submit(ProcessJobRequest request)
    {
        // Idempotency: the Job Service may retry a dispatch whose response it never received.
        if (store.TryGet(request.JobId, out var existing))
        {
            return new SubmitResult(SubmitOutcome.AlreadyExists, existing);
        }

        var job = new ProcessingJob(request.JobId, request.Documents, Correlation.CurrentId, time.GetUtcNow());
        if (!store.TryAdd(job))
        {
            // Lost a race with a concurrent submit of the same job id.
            store.TryGet(request.JobId, out existing);
            return new SubmitResult(SubmitOutcome.AlreadyExists, existing);
        }

        if (!queue.TryEnqueue(job))
        {
            store.Remove(job.JobId);
            logger.LogWarning("Queue full; rejecting job {JobId}", job.JobId);
            return new SubmitResult(SubmitOutcome.QueueFull, null);
        }

        logger.LogInformation("Job {JobId} accepted with {DocumentCount} documents", job.JobId, job.TotalDocuments);
        return new SubmitResult(SubmitOutcome.Accepted, job);
    }

    public ProcessingJob? Get(Guid jobId) => store.TryGet(jobId, out var job) ? job : null;

    public IReadOnlyCollection<ProcessingJob> List() => store.GetAll();

    public (CancelOutcome Outcome, ProcessingJob? Job) Cancel(Guid jobId)
    {
        if (!store.TryGet(jobId, out var job))
        {
            return (CancelOutcome.NotFound, null);
        }

        if (job.TryCancel(time.GetUtcNow()))
        {
            logger.LogInformation("Job {JobId} cancelled", jobId);
            events.JobCancelled(job);
            return (CancelOutcome.Cancelled, job);
        }

        // Cancelling twice is not an error (idempotent); cancelling a finished job is.
        return job.Status == ProcessingStatus.Cancelled
            ? (CancelOutcome.Cancelled, job)
            : (CancelOutcome.AlreadyFinished, job);
    }
}
