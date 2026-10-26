using Ta9.ProcessorService.Application;

namespace Ta9.ProcessorService.Infrastructure;

/// <summary>
/// Placeholder publisher that only logs lifecycle events.
/// Replaced by the WebSocket publisher (with outbox and ack) in the next step.
/// </summary>
public sealed class LoggingJobEventPublisher(ILogger<LoggingJobEventPublisher> logger) : IJobEventPublisher
{
    public void JobStarted(ProcessingJob job) =>
        logger.LogDebug("Event JobStarted {JobId}", job.JobId);

    public void JobProgress(ProcessingJob job) =>
        logger.LogDebug("Event JobProgress {JobId} {Processed}/{Total}", job.JobId, job.ProcessedDocuments, job.TotalDocuments);

    public void JobCompleted(ProcessingJob job) =>
        logger.LogDebug("Event JobCompleted {JobId}", job.JobId);

    public void JobFailed(ProcessingJob job) =>
        logger.LogDebug("Event JobFailed {JobId}: {Reason}", job.JobId, job.FailureReason);

    public void JobCancelled(ProcessingJob job) =>
        logger.LogDebug("Event JobCancelled {JobId}", job.JobId);
}
