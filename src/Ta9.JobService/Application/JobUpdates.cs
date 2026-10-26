using System.Collections.Concurrent;
using System.Threading.Channels;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Application;

/// <summary>In-process fan-out of job changes to live subscribers (SSE clients).</summary>
public interface IJobUpdateBroadcaster
{
    /// <summary>Never blocks: slow subscribers lose intermediate snapshots, not the latest one.</summary>
    void Publish(Job job);

    JobUpdateSubscription Subscribe(Guid jobId);
}

public sealed class JobUpdateSubscription(ChannelReader<Job> reader, Action unsubscribe) : IDisposable
{
    private int _disposed;

    /// <summary>Snapshots of the job, in publish order. Completes after the job reaches a terminal state.</summary>
    public ChannelReader<Job> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unsubscribe();
        }
    }
}

/// <summary>
/// Single-instance broadcaster. Each subscriber has a small bounded channel that drops the oldest snapshot
/// when full: every message is a full snapshot, so losing an intermediate one is harmless and a slow client
/// can never block the writers. (Multi-instance deployments would put Redis pub/sub or a broker here.)
/// </summary>
public sealed class InMemoryJobUpdateBroadcaster : IJobUpdateBroadcaster
{
    private const int SubscriberBufferSize = 16;

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<Job>>> _subscribers = new();

    public void Publish(Job job)
    {
        if (!_subscribers.TryGetValue(job.Id, out var subscribers))
        {
            return;
        }

        var snapshot = job.Clone();
        foreach (var channel in subscribers.Values)
        {
            channel.Writer.TryWrite(snapshot);
        }

        if (snapshot.IsTerminal)
        {
            // Nothing else will happen to this job: release its subscribers.
            _subscribers.TryRemove(job.Id, out _);
            foreach (var channel in subscribers.Values)
            {
                channel.Writer.TryComplete();
            }
        }
    }

    public JobUpdateSubscription Subscribe(Guid jobId)
    {
        var channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(SubscriberBufferSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var key = Guid.NewGuid();
        var subscribers = _subscribers.GetOrAdd(jobId, _ => new ConcurrentDictionary<Guid, Channel<Job>>());
        subscribers[key] = channel;

        return new JobUpdateSubscription(channel.Reader, () =>
        {
            if (subscribers.TryRemove(key, out var removed))
            {
                removed.Writer.TryComplete();
            }
        });
    }

    /// <summary>Number of jobs with live subscribers (diagnostics/tests).</summary>
    public int WatchedJobs => _subscribers.Count(pair => !pair.Value.IsEmpty);
}

/// <summary>
/// Decorator that publishes every successful job write. All writers (API, dispatcher, WS handler, reconciler)
/// go through the repository, so none of them can forget to notify subscribers.
/// </summary>
public sealed class BroadcastingJobRepository(IJobRepository inner, IJobUpdateBroadcaster broadcaster) : IJobRepository
{
    public async Task InsertAsync(Job job, CancellationToken cancellationToken)
    {
        await inner.InsertAsync(job, cancellationToken);
        broadcaster.Publish(job);
    }

    public async Task<bool> TryUpdateAsync(Job job, CancellationToken cancellationToken)
    {
        var saved = await inner.TryUpdateAsync(job, cancellationToken);
        if (saved)
        {
            broadcaster.Publish(job);
        }

        return saved;
    }

    public Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);

    public Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken cancellationToken) =>
        inner.ListAsync(status, page, pageSize, cancellationToken);

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => inner.DeleteAsync(id, cancellationToken);

    /// <summary>Not published: a lease is dispatcher bookkeeping, not a change clients care about.</summary>
    public Task<Job?> TryClaimNextForDispatchAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken) =>
        inner.TryClaimNextForDispatchAsync(now, leaseUntil, cancellationToken);

    public Task<IReadOnlyList<Job>> FindStaleActiveAsync(DateTimeOffset updatedBefore, int limit, CancellationToken cancellationToken) =>
        inner.FindStaleActiveAsync(updatedBefore, limit, cancellationToken);

    public Task<IReadOnlyList<Job>> FindUnpublishedTerminalAsync(int limit, CancellationToken cancellationToken) =>
        inner.FindUnpublishedTerminalAsync(limit, cancellationToken);
}
