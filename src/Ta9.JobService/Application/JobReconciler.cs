using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.JobService.Domain;
using Ta9.ServiceDefaults;

namespace Ta9.JobService.Application;

public sealed class ReconcileOptions
{
    public const string SectionName = "Reconcile";

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A Queued/Processing job without any update (event or progress) for this long is checked.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How many times a job lost by the Processor is dispatched again before it is marked Failed.</summary>
    [Range(0, 100)]
    public int MaxRedispatches { get; set; } = 3;

    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 100;
}

public sealed record ReconcileReport(int Checked, int StillRunning, int Recovered, int Redispatched, int Failed, int Unreachable);

/// <summary>
/// Safety net for lost events and Processor restarts. Periodically asks the Processor about active jobs that
/// have been silent for too long and converges the job to the Processor's truth:
/// <list type="bullet">
/// <item>still running → refresh progress/liveness;</item>
/// <item>finished → apply the result (same rules as the WS event);</item>
/// <item>unknown to the Processor (it restarted) → back to Pending for redispatch, or Failed after N redispatches;</item>
/// <item>Processor unreachable → leave the job alone (it is not the job's fault).</item>
/// </list>
/// </summary>
public sealed class JobReconciler(
    IJobRepository jobs,
    IProcessorClient processor,
    ProcessorEventHandler stateUpdater,
    IJobDispatchSignal dispatchSignal,
    IOptions<ReconcileOptions> options,
    TimeProvider time,
    ILogger<JobReconciler> logger) : BackgroundService
{
    private enum Outcome
    {
        StillRunning,
        Recovered,
        Redispatched,
        Failed,
        Unreachable,
        Skipped
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogWarning("Job reconciliation is disabled by configuration");
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var report = await ReconcileOnceAsync(stoppingToken);
                    if (report.Checked > 0)
                    {
                        logger.LogInformation("Reconciliation: {@Report}", report);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Reconciliation pass failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public async Task<ReconcileReport> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var staleBefore = time.GetUtcNow() - settings.StaleAfter;
        var stale = await jobs.FindStaleActiveAsync(staleBefore, settings.BatchSize, cancellationToken);

        var outcomes = new List<Outcome>(stale.Count);
        foreach (var job in stale)
        {
            try
            {
                outcomes.Add(await ReconcileAsync(job, staleBefore, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not reconcile job {JobId}", job.Id);
            }
        }

        return new ReconcileReport(
            Checked: stale.Count,
            StillRunning: outcomes.Count(o => o == Outcome.StillRunning),
            Recovered: outcomes.Count(o => o == Outcome.Recovered),
            Redispatched: outcomes.Count(o => o == Outcome.Redispatched),
            Failed: outcomes.Count(o => o == Outcome.Failed),
            Unreachable: outcomes.Count(o => o == Outcome.Unreachable));
    }

    private async Task<Outcome> ReconcileAsync(Job job, DateTimeOffset staleBefore, CancellationToken cancellationToken)
    {
        using var correlation = Correlation.BeginScope(job.CorrelationId ?? Correlation.NewId());
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id });

        var lookup = await processor.GetAsync(job.Id, cancellationToken);
        switch (lookup.Outcome)
        {
            case LookupOutcome.Found:
                await stateUpdater.ApplyRemoteStatusAsync(job.Id, lookup.Status!, cancellationToken);
                return lookup.Status!.Status is ProcessingStatus.Queued or ProcessingStatus.Processing
                    ? Outcome.StillRunning
                    : Outcome.Recovered;

            case LookupOutcome.NotFound:
                return await RecoverLostJobAsync(job.Id, staleBefore, cancellationToken);

            default:
                logger.LogWarning("Processor unreachable while reconciling job {JobId}: {Error}", job.Id, lookup.Error);
                return Outcome.Unreachable;
        }
    }

    private async Task<Outcome> RecoverLostJobAsync(Guid jobId, DateTimeOffset staleBefore, CancellationToken cancellationToken)
    {
        var maxRedispatches = options.Value.MaxRedispatches;
        var now = time.GetUtcNow();
        var update = await jobs.UpdateAsync(jobId, job =>
        {
            // Re-check under optimistic concurrency: an event may have arrived since the stale query.
            if (job.UpdatedAt >= staleBefore || job.Status is not (JobStatus.Queued or JobStatus.Processing))
            {
                return false;
            }

            return job.RedispatchCount >= maxRedispatches
                ? job.TryFail($"Processor lost the job {job.RedispatchCount + 1} times", now)
                : job.TryReturnToPending(now);
        }, cancellationToken);

        if (update.Outcome != JobUpdateOutcome.Applied)
        {
            return Outcome.Skipped;
        }

        if (update.Job!.Status == JobStatus.Failed)
        {
            logger.LogError("Job {JobId} failed: {Reason}", jobId, update.Job.FailureReason);
            return Outcome.Failed;
        }

        logger.LogWarning("Processor no longer knows job {JobId}; redispatching (attempt {Redispatch})", jobId, update.Job.RedispatchCount);
        dispatchSignal.Notify();
        return Outcome.Redispatched;
    }
}
