using System.Collections.Concurrent;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Infrastructure.Storage.InMemory;

/// <summary>
/// Process-local storage with the same semantics as the Mongo implementation (copies in/out,
/// optimistic concurrency). Used for tests and for running the service without a database.
/// </summary>
public sealed class InMemoryJobRepository : IJobRepository
{
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly object _writeGate = new();

    public Task InsertAsync(Job job, CancellationToken cancellationToken)
    {
        if (!_jobs.TryAdd(job.Id, job.Clone()))
        {
            throw new InvalidOperationException($"Job {job.Id} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.TryGetValue(id, out var job) ? job.Clone() : null);

    public Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matching = _jobs.Values
            .Where(job => status is null || job.Status == status)
            .OrderByDescending(job => job.CreatedAt)
            .ToList();

        var items = matching
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(job => job.Clone())
            .ToList();

        return Task.FromResult(new PagedResult<Job>(items, page, pageSize, matching.Count));
    }

    public Task<bool> TryUpdateAsync(Job job, CancellationToken cancellationToken)
    {
        lock (_writeGate)
        {
            if (!_jobs.TryGetValue(job.Id, out var stored) || stored.Version != job.Version)
            {
                return Task.FromResult(false);
            }

            job.Version++;
            _jobs[job.Id] = job.Clone();
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.TryRemove(id, out _));

    public Task<Job?> TryClaimNextForDispatchAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        lock (_writeGate)
        {
            var candidate = _jobs.Values
                .Where(job => job.Status == JobStatus.Pending
                              && job.NextDispatchAt <= now
                              && (job.LeaseUntil is null || job.LeaseUntil <= now))
                .OrderBy(job => job.NextDispatchAt)
                .FirstOrDefault();

            if (candidate is null)
            {
                return Task.FromResult<Job?>(null);
            }

            var claimed = candidate.Clone();
            claimed.LeaseUntil = leaseUntil;
            claimed.Version++;
            _jobs[claimed.Id] = claimed;
            return Task.FromResult<Job?>(claimed.Clone());
        }
    }
}
