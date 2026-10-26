using System.Text.Json;
using Ta9.Contracts.Events;
using Ta9.JobService.Domain;
using Ta9.ServiceDefaults;

namespace Ta9.JobService.Application;

public enum EventHandlingOutcome
{
    /// <summary>The event changed the job.</summary>
    Applied,

    /// <summary>Duplicate, out of order, unknown job/type or illegal transition: nothing to do.</summary>
    Ignored
}

/// <summary>
/// Applies Processor lifecycle events to jobs. Every path is idempotent: replays, duplicates and late events
/// are absorbed by the job state machine, so the caller can acknowledge both outcomes.
/// Storage failures propagate as exceptions: the event must NOT be acknowledged and the Processor will resend it.
/// </summary>
public sealed class ProcessorEventHandler(
    IJobRepository jobs,
    IJobDocumentRepository documents,
    TimeProvider time,
    ILogger<ProcessorEventHandler> logger)
{
    public async Task<EventHandlingOutcome> HandleAsync(ProcessorEventEnvelope envelope, CancellationToken cancellationToken)
    {
        using var correlation = Correlation.BeginScope(
            Correlation.IsValid(envelope.CorrelationId) ? envelope.CorrelationId! : Correlation.NewId());
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = envelope.JobId });

        if (envelope.SchemaVersion > ProcessorEventSerializer.CurrentSchemaVersion)
        {
            logger.LogWarning("Ignoring {Type} with unsupported schema version {Version}", envelope.Type, envelope.SchemaVersion);
            return EventHandlingOutcome.Ignored;
        }

        try
        {
            var now = time.GetUtcNow();
            var outcome = envelope.Type switch
            {
                ProcessorEventTypes.JobStarted => await UpdateAsync(envelope, job => job.TryStart(now), cancellationToken),
                ProcessorEventTypes.JobProgress => await HandleProgressAsync(envelope, now, cancellationToken),
                ProcessorEventTypes.JobCompleted => await HandleCompletedAsync(envelope, now, cancellationToken),
                ProcessorEventTypes.JobFailed => await HandleFailedAsync(envelope, now, cancellationToken),
                ProcessorEventTypes.JobCancelled => await UpdateAsync(envelope, job => job.TryCancel(now), cancellationToken),
                _ => LogUnknownType(envelope)
            };

            if (outcome == EventHandlingOutcome.Applied && envelope.Type != ProcessorEventTypes.JobProgress)
            {
                logger.LogInformation("Applied {Type} to job {JobId}", envelope.Type, envelope.JobId);
            }

            return outcome;
        }
        catch (JsonException ex)
        {
            // A malformed payload will be malformed on every retry too: acknowledge and move on.
            logger.LogError(ex, "Ignoring {Type} with an invalid payload", envelope.Type);
            return EventHandlingOutcome.Ignored;
        }
    }

    private Task<EventHandlingOutcome> HandleProgressAsync(ProcessorEventEnvelope envelope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = ProcessorEventSerializer.GetPayload<JobProgressPayload>(envelope);
        return UpdateAsync(envelope, job => job.RecordProgress(payload.ProcessedDocuments, now), cancellationToken);
    }

    private async Task<EventHandlingOutcome> HandleCompletedAsync(ProcessorEventEnvelope envelope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = ProcessorEventSerializer.GetPayload<JobCompletedPayload>(envelope);

        var current = await jobs.GetAsync(envelope.JobId, cancellationToken);
        if (current is null || current.IsTerminal)
        {
            // Unknown, already completed (replay) or cancelled/failed meanwhile: don't touch the results.
            return LogIgnored(envelope, current);
        }

        // Results first, status second: a client that sees Completed is guaranteed to find the results.
        // Writing results is idempotent, so a crash between the two steps is repaired by the redelivery.
        await documents.SaveResultsAsync(envelope.JobId, payload.Results, cancellationToken);
        return await UpdateAsync(envelope, job => job.TryComplete(payload.Summary, now), cancellationToken);
    }

    private Task<EventHandlingOutcome> HandleFailedAsync(ProcessorEventEnvelope envelope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = ProcessorEventSerializer.GetPayload<JobFailedPayload>(envelope);
        return UpdateAsync(envelope, job => job.TryFail($"{payload.ErrorCode}: {payload.Reason}", now), cancellationToken);
    }

    /// <summary>
    /// Applies <paramref name="apply"/> with optimistic concurrency. A Pending job is first moved to Queued:
    /// an event from the Processor proves it accepted the job, even if the dispatcher has not recorded the
    /// 202 yet (small jobs can finish before the dispatch response is processed).
    /// </summary>
    private async Task<EventHandlingOutcome> UpdateAsync(ProcessorEventEnvelope envelope, Func<Job, bool> apply, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var update = await jobs.UpdateAsync(
            envelope.JobId,
            job => (job.Status != JobStatus.Pending || job.TryMarkQueued(now)) && apply(job),
            cancellationToken);

        return update.Outcome == JobUpdateOutcome.Applied
            ? EventHandlingOutcome.Applied
            : LogIgnored(envelope, update.Job);
    }

    private EventHandlingOutcome LogIgnored(ProcessorEventEnvelope envelope, Job? job)
    {
        if (job is null)
        {
            logger.LogWarning("Ignoring {Type} for unknown job {JobId}", envelope.Type, envelope.JobId);
        }
        else
        {
            logger.LogDebug("Ignoring {Type} for job {JobId} in status {Status} (duplicate or out of order)",
                envelope.Type, envelope.JobId, job.Status);
        }

        return EventHandlingOutcome.Ignored;
    }

    private EventHandlingOutcome LogUnknownType(ProcessorEventEnvelope envelope)
    {
        logger.LogWarning("Ignoring unknown event type {Type}", envelope.Type);
        return EventHandlingOutcome.Ignored;
    }
}
