using Ta9.Contracts.Events;
using Ta9.ProcessorService.Application;

namespace Ta9.ProcessorService.Infrastructure.Events;

/// <summary>
/// Turns job lifecycle changes into WS event envelopes and hands them to the <see cref="EventOutbox"/>.
/// Never blocks and never throws because of delivery problems: the outbox and the client handle delivery.
/// </summary>
public sealed class WebSocketJobEventPublisher(
    EventOutbox outbox,
    TimeProvider time,
    ILogger<WebSocketJobEventPublisher> logger) : IJobEventPublisher
{
    public void JobStarted(ProcessingJob job) =>
        AddLifecycle(job, ProcessorEventTypes.JobStarted, new JobStartedPayload(job.TotalDocuments));

    public void JobProgress(ProcessingJob job) =>
        outbox.OfferProgress(Create(job, ProcessorEventTypes.JobProgress, new JobProgressPayload(job.ProcessedDocuments, job.TotalDocuments)));

    public void JobCompleted(ProcessingJob job) =>
        AddLifecycle(job, ProcessorEventTypes.JobCompleted, new JobCompletedPayload(job.Summary!, job.Results!));

    public void JobFailed(ProcessingJob job) =>
        AddLifecycle(job, ProcessorEventTypes.JobFailed, new JobFailedPayload("ProcessingError", job.FailureReason ?? "Unknown error"));

    public void JobCancelled(ProcessingJob job) =>
        AddLifecycle(job, ProcessorEventTypes.JobCancelled, new JobCancelledPayload());

    private void AddLifecycle<TPayload>(ProcessingJob job, string type, TPayload payload)
    {
        if (outbox.TryAddLifecycle(Create(job, type, payload)))
        {
            logger.LogDebug("Queued {Type} for job {JobId} ({Pending} events pending)", type, job.JobId, outbox.PendingCount);
        }
        else
        {
            logger.LogError(
                "Event outbox is full; dropping {Type} for job {JobId}. The Job Service reconciler will recover the job state.",
                type, job.JobId);
        }
    }

    private ProcessorEventEnvelope Create<TPayload>(ProcessingJob job, string type, TPayload payload) =>
        ProcessorEventSerializer.Create(type, job.JobId, payload, time.GetUtcNow(), job.CorrelationId);
}
