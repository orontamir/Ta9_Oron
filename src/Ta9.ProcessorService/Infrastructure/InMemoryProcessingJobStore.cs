using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Ta9.ProcessorService.Application;

namespace Ta9.ProcessorService.Infrastructure;

public sealed class InMemoryProcessingJobStore : IProcessingJobStore
{
    private readonly ConcurrentDictionary<Guid, ProcessingJob> _jobs = new();

    public bool TryAdd(ProcessingJob job) => _jobs.TryAdd(job.JobId, job);

    public bool TryGet(Guid jobId, [NotNullWhen(true)] out ProcessingJob? job) => _jobs.TryGetValue(jobId, out job);

    public IReadOnlyCollection<ProcessingJob> GetAll() => _jobs.Values.ToList();

    public void Remove(Guid jobId) => _jobs.TryRemove(jobId, out _);

    public int RemoveFinishedBefore(DateTimeOffset cutoff)
    {
        var removed = 0;
        foreach (var (jobId, job) in _jobs)
        {
            if (job.IsTerminal && job.CompletedAt < cutoff && _jobs.TryRemove(jobId, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}
