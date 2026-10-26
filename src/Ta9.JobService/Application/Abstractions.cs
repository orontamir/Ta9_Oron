using Ta9.Contracts.Processing;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Application;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

/// <summary>Read model for the results endpoint (no document content).</summary>
public sealed record JobDocumentResult(int Index, string Name, DocumentResultDto? Result);

public interface IJobRepository
{
    Task InsertAsync(Job job, CancellationToken cancellationToken);

    Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Newest first. <paramref name="page"/> is 1-based.</summary>
    Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Optimistic concurrency: saves only if the stored version still equals <see cref="Job.Version"/>.
    /// On success the job's version is incremented; on conflict returns false and nothing is written.
    /// </summary>
    Task<bool> TryUpdateAsync(Job job, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims the Pending job that has been due the longest (<c>NextDispatchAt ≤ now</c>, no live lease)
    /// by setting <see cref="Job.LeaseUntil"/>. Returns null when nothing is due. Two concurrent callers never get the same job.
    /// </summary>
    Task<Job?> TryClaimNextForDispatchAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken);
}

public interface IJobDocumentRepository
{
    Task InsertManyAsync(IReadOnlyList<JobDocument> documents, CancellationToken cancellationToken);

    /// <summary>All documents of a job (with content), ordered by index. Used to build the dispatch payload.</summary>
    Task<IReadOnlyList<JobDocument>> GetByJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Idempotent: writing the same results twice leaves the same state.</summary>
    Task SaveResultsAsync(Guid jobId, IReadOnlyList<DocumentResultDto> results, CancellationToken cancellationToken);

    Task<PagedResult<JobDocumentResult>> GetResultsAsync(Guid jobId, int page, int pageSize, CancellationToken cancellationToken);

    Task DeleteByJobAsync(Guid jobId, CancellationToken cancellationToken);
}
