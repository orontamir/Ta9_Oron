using Ta9.JobService.Domain;
using Ta9.ServiceDefaults;

namespace Ta9.JobService.Application;

public sealed record NewDocument(string Name, string Content);

public sealed record CreateJobCommand(string? Name, IReadOnlyList<NewDocument> Documents);

public enum ResultsOutcome
{
    Found,
    NotFound,

    /// <summary>The job exists but has not completed, so there are no results to return.</summary>
    NotCompleted
}

public sealed record JobResults(ResultsOutcome Outcome, Job? Job, PagedResult<JobDocumentResult>? Page);

public enum CancelJobOutcome
{
    Cancelled,
    NotFound,
    AlreadyFinished
}

public sealed record CancelJobResult(CancelJobOutcome Outcome, Job? Job);

public enum DeleteJobOutcome
{
    Deleted,
    NotFound,

    /// <summary>Active jobs still own work in the Processor; cancel first.</summary>
    StillActive
}

/// <summary>Use-cases behind /api/jobs.</summary>
public sealed class JobManager(
    IJobRepository jobs,
    IJobDocumentRepository documents,
    IProcessorClient processor,
    IJobDispatchSignal dispatchSignal,
    TimeProvider time,
    ILogger<JobManager> logger)
{
    public async Task<Job> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken)
    {
        var job = Job.Create(Guid.NewGuid(), command.Name, command.Documents.Count, Correlation.CurrentId, time.GetUtcNow());
        var jobDocuments = command.Documents
            .Select((document, index) => JobDocument.Create(job.Id, index, document.Name, document.Content))
            .ToList();

        // Documents first, job second: the job record is the commit marker. A crash in between leaves
        // only unreferenced documents, never a job without its documents.
        await documents.InsertManyAsync(jobDocuments, cancellationToken);
        logger.LogDebug("Stored {DocumentCount} documents of job {JobId}", jobDocuments.Count, job.Id);
        await jobs.InsertAsync(job, cancellationToken);

        logger.LogInformation("Job {JobId} created with {DocumentCount} documents", job.Id, job.TotalDocuments);
        dispatchSignal.Notify();
        return job;
    }

    public Task<Job?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        jobs.GetAsync(jobId, cancellationToken);

    public Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken cancellationToken) =>
        jobs.ListAsync(status, page, pageSize, cancellationToken);

    public async Task<JobResults> GetResultsAsync(Guid jobId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        if (job is null)
        {
            return new JobResults(ResultsOutcome.NotFound, null, null);
        }

        if (job.Status != JobStatus.Completed)
        {
            return new JobResults(ResultsOutcome.NotCompleted, job, null);
        }

        var results = await documents.GetResultsAsync(jobId, page, pageSize, cancellationToken);
        return new JobResults(ResultsOutcome.Found, job, results);
    }

    public async Task<CancelJobResult> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var previousStatus = JobStatus.Pending;
        var update = await jobs.UpdateAsync(jobId, job =>
        {
            previousStatus = job.Status;
            return job.TryCancel(now);
        }, cancellationToken);

        switch (update.Outcome)
        {
            case JobUpdateOutcome.NotFound:
                return new CancelJobResult(CancelJobOutcome.NotFound, null);
            case JobUpdateOutcome.Applied:
                logger.LogInformation("Job {JobId} cancelled (was {PreviousStatus})", jobId, previousStatus);
                if (previousStatus is JobStatus.Queued or JobStatus.Processing)
                {
                    // The user-visible state is already Cancelled; stopping the work is best effort
                    // (a late JobCompleted is ignored by the state machine anyway).
                    await processor.CancelAsync(jobId, cancellationToken);
                }
                return new CancelJobResult(CancelJobOutcome.Cancelled, update.Job);
            default:
                // Cancelling twice is idempotent; cancelling a completed/failed job is a conflict.
                return update.Job!.Status == JobStatus.Cancelled
                    ? new CancelJobResult(CancelJobOutcome.Cancelled, update.Job)
                    : new CancelJobResult(CancelJobOutcome.AlreadyFinished, update.Job);
        }
    }

    public async Task<DeleteJobOutcome> DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        if (job is null)
        {
            return DeleteJobOutcome.NotFound;
        }

        if (!job.IsTerminal)
        {
            return DeleteJobOutcome.StillActive;
        }

        // Job first, documents second (mirror of create): a crash in between leaves only orphan documents.
        await jobs.DeleteAsync(jobId, cancellationToken);
        await documents.DeleteByJobAsync(jobId, cancellationToken);

        logger.LogInformation("Job {JobId} deleted", jobId);
        return DeleteJobOutcome.Deleted;
    }
}
