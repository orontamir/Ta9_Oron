using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Application;

public class InMemoryJobUpdateBroadcasterTests
{
    private readonly InMemoryJobUpdateBroadcaster _broadcaster = new();

    private static List<Job> Drain(JobUpdateSubscription subscription)
    {
        var received = new List<Job>();
        while (subscription.Reader.TryRead(out var job))
        {
            received.Add(job);
        }
        return received;
    }

    [Fact]
    public void Subscribers_receive_snapshots_of_their_job_only()
    {
        var job = TestJobs.New();
        var other = TestJobs.New();
        using var subscription = _broadcaster.Subscribe(job.Id);

        _broadcaster.Publish(other);
        _broadcaster.Publish(job);

        Drain(subscription).Should().ContainSingle().Which.Id.Should().Be(job.Id);
    }

    [Fact]
    public void Published_snapshot_is_a_copy()
    {
        var job = TestJobs.New();
        using var subscription = _broadcaster.Subscribe(job.Id);

        _broadcaster.Publish(job);
        job.TryCancel(TestJobs.T0);

        Drain(subscription).Single().Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public void Every_subscriber_gets_the_update()
    {
        var job = TestJobs.New();
        using var first = _broadcaster.Subscribe(job.Id);
        using var second = _broadcaster.Subscribe(job.Id);

        _broadcaster.Publish(job);

        Drain(first).Should().HaveCount(1);
        Drain(second).Should().HaveCount(1);
    }

    [Fact]
    public void Disposed_subscription_stops_receiving_and_completes()
    {
        var job = TestJobs.New();
        var subscription = _broadcaster.Subscribe(job.Id);

        subscription.Dispose();
        _broadcaster.Publish(job);

        Drain(subscription).Should().BeEmpty();
        subscription.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void Terminal_update_is_delivered_then_subscribers_are_released()
    {
        var job = TestJobs.InStatus(JobStatus.Processing);
        using var subscription = _broadcaster.Subscribe(job.Id);

        job.TryComplete(TestJobs.Summary(), TestJobs.T0);
        _broadcaster.Publish(job);

        Drain(subscription).Single().Status.Should().Be(JobStatus.Completed);
        subscription.Reader.Completion.IsCompleted.Should().BeTrue();
        _broadcaster.WatchedJobs.Should().Be(0);
    }

    [Fact]
    public void Slow_subscriber_keeps_the_latest_snapshots_and_never_blocks_the_publisher()
    {
        var job = TestJobs.InStatus(JobStatus.Processing);
        using var subscription = _broadcaster.Subscribe(job.Id);

        for (var version = 1; version <= 100; version++)
        {
            job.Version = version;
            _broadcaster.Publish(job); // must not block although nobody reads
        }

        var received = Drain(subscription);
        received.Count.Should().BeLessThanOrEqualTo(16);
        received.Last().Version.Should().Be(100);
    }
}

public class BroadcastingJobRepositoryTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly InMemoryJobUpdateBroadcaster _broadcaster = new();
    private readonly BroadcastingJobRepository _repository;

    public BroadcastingJobRepositoryTests() =>
        _repository = new BroadcastingJobRepository(new InMemoryJobRepository(), _broadcaster);

    [Fact]
    public async Task Successful_update_is_published_with_the_new_version()
    {
        var job = TestJobs.New();
        await _repository.InsertAsync(job, None);
        using var subscription = _broadcaster.Subscribe(job.Id);

        job.TryMarkQueued(TestJobs.T0);
        await _repository.TryUpdateAsync(job, None);

        subscription.Reader.TryRead(out var published).Should().BeTrue();
        published!.Status.Should().Be(JobStatus.Queued);
        published.Version.Should().Be(1);
    }

    [Fact]
    public async Task Rejected_update_is_not_published()
    {
        var job = TestJobs.New();
        await _repository.InsertAsync(job, None);
        using var subscription = _broadcaster.Subscribe(job.Id);
        var stale = job.Clone();
        job.TryMarkQueued(TestJobs.T0);
        await _repository.TryUpdateAsync(job, None);
        subscription.Reader.TryRead(out _);

        stale.TryCancel(TestJobs.T0);
        (await _repository.TryUpdateAsync(stale, None)).Should().BeFalse();

        subscription.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Dispatch_claims_are_not_published()
    {
        var job = TestJobs.New();
        await _repository.InsertAsync(job, None);
        using var subscription = _broadcaster.Subscribe(job.Id);

        await _repository.TryClaimNextForDispatchAsync(TestJobs.T0, TestJobs.T0.AddMinutes(1), None);

        subscription.Reader.TryRead(out _).Should().BeFalse();
    }
}
