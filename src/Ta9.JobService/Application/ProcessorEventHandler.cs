using System.Text.Json;
using Ta9.Contracts.Events;
using Ta9.Contracts.Processing;
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
/// Applies what the Processor reports (WS events, or a status snapshot fetched by the reconciler) to jobs.
/// Every path is idempotent: replays, duplicates and late events are absorbed by the job state machine, so
/// the caller can acknowledge both outcomes. Storage failures propagate as exceptions: the event must NOT be
/// acknowledged and the Processor will resend it.
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
        logger.LogDebug("Received {Type} {EventId} for job {JobId} (schema {Version})", envelope.Type, envelope.EventId, envelope.JobId, envelope.SchemaVersion);

        if (envelope.SchemaVersion > ProcessorEventSerializer.CurrentSchemaVersion)
        {
            logger.LogWarning("Ignoring {Type} with unsupported schema version {Version}", envelope.Type, envelope.SchemaVersion);
            return EventHandlingOutcome.Ignored;
        }

        try
        {
            var now = time.GetUtcNow();
            var jobId = envelope.JobId;
            var type = envelope.Type;
            var outcome = type switch
            {
                ProcessorEventTypes.JobStarted =>
                    await UpdateAsync(jobId, type, job => job.TryStart(now), cancellationToken),
                ProcessorEventTypes.JobProgress =>
                    await UpdateAsync(jobId, type, job => job.RecordProgress(Payload<JobProgressPayload>(envelope).ProcessedDocuments, now), cancellationToken),
                ProcessorEventTypes.JobCompleted =>
                    await CompleteAsync(jobId, type, Payload<JobCompletedPayload>(envelope), now, cancellationToken),
                ProcessorEventTypes.JobFailed =>
                    await FailAsync(jobId, type, Payload<JobFailedPayload>(envelope), now, cancellationToken),
                ProcessorEventTypes.JobCancelled =>
                    await UpdateAsync(jobId, type, job => job.TryCancel(now), cancellationToken),
                _ => LogUnknownType(type)
            };

            if (outcome == EventHandlingOutcome.Applied && type != ProcessorEventTypes.JobProgress)
            {
                logger.LogInformation("Applied {Type} to job {JobId}", type, jobId);
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

    /// <summary>
    /// Applies a status snapshot from GET /api/process/{id} (used by the reconciler when events were lost).
    /// Same rules as the events; for a still-running job it refreshes progress and the liveness timestamp.
    /// </summary>
    public async Task<EventHandlingOutcome> ApplyRemoteStatusAsync(Guid jobId, ProcessJobStatusResponse remote, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var source = $"remote status {remote.Status}";
        return remote.Status switch
        {
            ProcessingStatus.Processing =>
                await UpdateAsync(jobId, source, job => job.RecordProgress(remote.ProcessedDocuments, now), cancellationToken),
            ProcessingStatus.Completed when remote.Summary is not null && remote.Results is not null =>
                await CompleteAsync(jobId, source, new JobCompletedPayload(remote.Summary, remote.Results), now, cancellationToken),
            ProcessingStatus.Failed =>
                await FailAsync(jobId, source, new JobFailedPayload("ProcessingError", remote.FailureReason ?? "Unknown error"), now, cancellationToken),
            ProcessingStatus.Cancelled =>
                await UpdateAsync(jobId, source, job => job.TryCancel(now), cancellationToken),
            // Queued (or Completed without results): the processor has it; just record that it is alive.
            _ => await UpdateAsync(jobId, source, job => job.Touch(now), cancellationToken)
        };
    }

    private async Task<EventHandlingOutcome> CompleteAsync(
        Guid jobId, string source, JobCompletedPayload payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = await jobs.GetAsync(jobId, cancellationToken);
        if (current is null || current.IsTerminal)
        {
            // Unknown, already completed (replay) or cancelled/failed meanwhile: don't touch the results.
            return LogIgnored(jobId, source, current);
        }

        // Results first, status second: a client that sees Completed is guaranteed to find the results.
        // Writing results is idempotent, so a crash between the two steps is repaired by the redelivery.
        await documents.SaveResultsAsync(jobId, payload.Results, cancellationToken);
        return await UpdateAsync(jobId, source, job => job.TryComplete(payload.Summary, now), cancellationToken);
    }

    private Task<EventHandlingOutcome> FailAsync(
        Guid jobId, string source, JobFailedPayload payload, DateTimeOffset now, CancellationToken cancellationToken) =>
        UpdateAsync(jobId, source, job => job.TryFail($"{payload.ErrorCode}: {payload.Reason}", now), cancellationToken);

    /// <summary>
    /// Applies <paramref name="apply"/> with optimistic concurrency. A Pending job is first moved to Queued:
    /// any report from the Processor proves it accepted the job, even if the dispatcher has not recorded the
    /// 202 yet (small jobs can finish before the dispatch response is processed).
    /// </summary>
    private async Task<EventHandlingOutcome> UpdateAsync(Guid jobId, string source, Func<Job, bool> apply, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var update = await jobs.UpdateAsync(
            jobId,
            job => (job.Status != JobStatus.Pending || job.TryMarkQueued(now)) && apply(job),
            cancellationToken);

        return update.Outcome == JobUpdateOutcome.Applied
            ? EventHandlingOutcome.Applied
            : LogIgnored(jobId, source, update.Job);
    }

    private static TPayload Payload<TPayload>(ProcessorEventEnvelope envelope) =>
        ProcessorEventSerializer.GetPayload<TPayload>(envelope);

    private EventHandlingOutcome LogIgnored(Guid jobId, string source, Job? job)
    {
        if (job is null)
        {
            logger.LogWarning("Ignoring {Source} for unknown job {JobId}", source, jobId);
        }
        else
        {
            logger.LogDebug("Ignoring {Source} for job {JobId} in status {Status} (duplicate or out of order)", source, jobId, job.Status);
        }

        return EventHandlingOutcome.Ignored;
    }

    private EventHandlingOutcome LogUnknownType(string type)
    {
        logger.LogWarning("Ignoring unknown event type {Type}", type);
        return EventHandlingOutcome.Ignored;
    }
}
