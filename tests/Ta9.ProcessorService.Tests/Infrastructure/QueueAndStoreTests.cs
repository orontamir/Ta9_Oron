using Microsoft.Extensions.Options;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Infrastructure;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Infrastructure;

public class ChannelJobQueueTests
{
    private static ChannelJobQueue CreateQueue(int capacity) =>
        new(Options.Create(new ProcessingOptions { QueueCapacity = capacity }));

    [Fact]
    public void Rejects_when_capacity_is_reached()
    {
        var queue = CreateQueue(2);

        queue.TryEnqueue(TestData.Job(TestData.Documents("a"))).Should().BeTrue();
        queue.TryEnqueue(TestData.Job(TestData.Documents("b"))).Should().BeTrue();
        queue.TryEnqueue(TestData.Job(TestData.Documents("c"))).Should().BeFalse();
    }

    [Fact]
    public async Task Delivers_jobs_in_fifo_order_and_frees_capacity()
    {
        var queue = CreateQueue(2);
        var first = TestData.Job(TestData.Documents("a"));
        var second = TestData.Job(TestData.Documents("b"));
        queue.TryEnqueue(first);
        queue.TryEnqueue(second);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = new List<ProcessingJob>();
        await foreach (var job in queue.ReadAllAsync(cts.Token))
        {
            read.Add(job);
            if (read.Count == 2)
            {
                break;
            }
        }

        read.Should().Equal(first, second);
        queue.TryEnqueue(TestData.Job(TestData.Documents("c"))).Should().BeTrue();
    }
}

public class InMemoryProcessingJobStoreTests
{
    private readonly InMemoryProcessingJobStore _store = new();

    [Fact]
    public void Rejects_duplicate_job_id()
    {
        var job = TestData.Job(TestData.Documents("a"));

        _store.TryAdd(job).Should().BeTrue();
        _store.TryAdd(job).Should().BeFalse();
        _store.TryGet(job.JobId, out var found).Should().BeTrue();
        found.Should().BeSameAs(job);
    }

    [Fact]
    public void Removes_only_finished_jobs_older_than_cutoff()
    {
        var time = new ManualTimeProvider();
        var oldFinished = TestData.Job(TestData.Documents("a"), time);
        oldFinished.TryCancel(time.GetUtcNow());
        time.Advance(TimeSpan.FromHours(2));
        var recentFinished = TestData.Job(TestData.Documents("b"), time);
        recentFinished.TryCancel(time.GetUtcNow());
        var running = TestData.Job(TestData.Documents("c"), time);
        running.TryStart(time.GetUtcNow());
        _store.TryAdd(oldFinished);
        _store.TryAdd(recentFinished);
        _store.TryAdd(running);

        var removed = _store.RemoveFinishedBefore(time.GetUtcNow() - TimeSpan.FromHours(1));

        removed.Should().Be(1);
        _store.GetAll().Select(j => j.JobId).Should().BeEquivalentTo(new[] { recentFinished.JobId, running.JobId });
    }
}
