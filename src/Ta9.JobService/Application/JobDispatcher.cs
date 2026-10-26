using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.JobService.Domain;
using Ta9.ServiceDefaults;

namespace Ta9.JobService.Application;

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    /// <summary>Lets tests (or a read-only replica) run the API without dispatching.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Failed attempts (unreachable / 5xx) before the job is marked Failed. 503 does not count.</summary>
    [Range(1, 100)]
    public int MaxDispatchAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Fallback poll when no new-job signal arrives (picks up retries that became due).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A claimed job is invisible to other dispatch loops for this long. Must exceed the HTTP timeout.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    [Range(1, 64)]
    public int MaxParallelDispatches { get; set; } = 4;
}

/// <summary>
/// Moves Pending jobs to the Processor. Jobs are claimed atomically with a lease (no double dispatch even with
/// several loops or instances), sent in bounded parallel batches, and the outcome is applied to the job:
/// accepted → Queued, busy → retry after the hint, unreachable → exponential backoff then Failed, rejected → Failed.
/// All scheduling state lives in the job record, so retries survive a restart of this service.
/// </summary>
public sealed class JobDispatcher(
    IJobRepository jobs,
    IJobDocumentRepository documents,
    IProcessorClient processor,
    IJobDispatchSignal signal,
    IOptions<DispatchOptions> options,
    TimeProvider time,
    ILogger<JobDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogWarning("Job dispatching is disabled by configuration");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueJobsAsync(stoppingToken);
                await signal.WaitAsync(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // e.g. storage down: log and try again on the next tick.
                logger.LogError(ex, "Dispatch loop iteration failed");
                await Task.Delay(options.Value.PollInterval, time, stoppingToken).ContinueWith(static _ => { }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>Dispatches every job that is due right now. Returns how many were handled.</summary>
    public async Task<int> DispatchDueJobsAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var handled = 0;
        while (true)
        {
            var batch = new List<Task>(settings.MaxParallelDispatches);
            while (batch.Count < settings.MaxParallelDispatches)
            {
                var now = time.GetUtcNow();
                var job = await jobs.TryClaimNextForDispatchAsync(now, now + settings.LeaseDuration, cancellationToken);
                if (job is null)
                {
                    break;
                }

                batch.Add(DispatchAsync(job, cancellationToken));
            }

            if (batch.Count == 0)
            {
                if (handled > 0)
                {
                    logger.LogDebug("Dispatch pass handled {Count} jobs", handled);
                }

                return handled;
            }

            await Task.WhenAll(batch);
            handled += batch.Count;
        }
    }

    private async Task DispatchAsync(Job job, CancellationToken cancellationToken)
    {
        using var correlation = Correlation.BeginScope(job.CorrelationId ?? Correlation.NewId());
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id });
        try
        {
            logger.LogDebug("Claimed job {JobId} for dispatch (previous attempts {Attempts}, lease until {LeaseUntil})",
                job.Id, job.DispatchAttempts, job.LeaseUntil);
            var payload = (await documents.GetByJobAsync(job.Id, cancellationToken))
                .Select(d => new ProcessDocumentRequest(d.Index, d.Name, d.Content))
                .ToList();

            var result = await processor.SubmitAsync(job.Id, payload, cancellationToken);
            logger.LogDebug("Processor answered {Outcome} for job {JobId} ({DocumentCount} documents)", result.Outcome, job.Id, payload.Count);
            await ApplyAsync(job.Id, result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The lease expires and the job is claimed again later.
            logger.LogError(ex, "Dispatch of job {JobId} failed unexpectedly; it will be retried after the lease expires", job.Id);
        }
    }

    private async Task ApplyAsync(Guid jobId, DispatchResult result, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();

        switch (result.Outcome)
        {
            case DispatchOutcome.Accepted:
            {
                var update = await jobs.UpdateAsync(jobId, job => job.TryMarkQueued(now), cancellationToken);
                if (update.Outcome == JobUpdateOutcome.Applied)
                {
                    logger.LogInformation("Job {JobId} dispatched to the processor", jobId);
                }
                else if (update.Job?.Status == JobStatus.Cancelled)
                {
                    // Cancelled while the dispatch request was in flight: tell the Processor to drop it.
                    logger.LogInformation("Job {JobId} was cancelled during dispatch; cancelling it on the processor", jobId);
                    await processor.CancelAsync(jobId, cancellationToken);
                }
                break;
            }

            case DispatchOutcome.Busy:
            {
                var retryAt = now + (result.RetryAfter ?? settings.InitialBackoff);
                await jobs.UpdateAsync(jobId, job => job.ScheduleDispatchRetry(retryAt, countAttempt: false, now), cancellationToken);
                logger.LogInformation("Processor busy; job {JobId} will be dispatched again at {RetryAt}", jobId, retryAt);
                break;
            }

            case DispatchOutcome.Unreachable:
            {
                var update = await jobs.UpdateAsync(jobId, job =>
                {
                    var attempt = job.DispatchAttempts + 1;
                    return attempt >= settings.MaxDispatchAttempts
                        ? job.TryFail($"Processor unreachable after {attempt} attempts: {result.Error}", now)
                        : job.ScheduleDispatchRetry(now + ExponentialBackoff.Compute(attempt, settings.InitialBackoff, settings.MaxBackoff), countAttempt: true, now);
                }, cancellationToken);

                if (update.Job?.Status == JobStatus.Failed)
                {
                    logger.LogError("Giving up on job {JobId}: {Reason}", jobId, update.Job.FailureReason);
                }
                else
                {
                    logger.LogWarning("Processor unreachable (attempt {Attempt}): {Error}. Next try at {RetryAt}",
                        update.Job?.DispatchAttempts, result.Error, update.Job?.NextDispatchAt);
                }
                break;
            }

            case DispatchOutcome.Rejected:
                logger.LogError("Processor rejected job {JobId}: {Error}", jobId, result.Error);
                await jobs.UpdateAsync(jobId, job => job.TryFail($"Processor rejected the job: {result.Error}", now), cancellationToken);
                break;
        }
    }
}
