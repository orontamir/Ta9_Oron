using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Domain;
using Ta9.ProcessorService.Infrastructure;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Application;

public class JobWorkerServiceTests
{
    private readonly RecordingJobEventPublisher _events = new();

    private (JobWorkerService Workers, ChannelJobQueue Queue) Create(ProcessingOptions options, IDocumentAnalyzer analyzer)
    {
        var wrapped = Options.Create(options);
        var queue = new ChannelJobQueue(wrapped);
        var runner = new JobRunner(analyzer, _events, wrapped, TimeProvider.System, NullLogger<JobRunner>.Instance);
        return (new JobWorkerService(queue, runner, wrapped, NullLogger<JobWorkerService>.Instance), queue);
    }

    [Fact]
    public async Task Processes_many_jobs_with_bounded_total_concurrency()
    {
        var options = new ProcessingOptions { MaxConcurrentJobs = 3, DocParallelismPerJob = 2, QueueCapacity = 100 };
        var analyzer = new ConcurrencyTrackingAnalyzer(TimeSpan.FromMilliseconds(5));
        var (workers, queue) = Create(options, analyzer);
        var jobs = Enumerable.Range(0, 12).Select(_ => TestData.Job(TestData.Documents(10))).ToList();

        await workers.StartAsync(CancellationToken.None);
        try
        {
            jobs.ForEach(job => queue.TryEnqueue(job).Should().BeTrue());

            await Eventually.TrueAsync(() => jobs.All(j => j.Status == ProcessingStatus.Completed), TimeSpan.FromSeconds(30));
        }
        finally
        {
            await workers.StopAsync(CancellationToken.None);
        }

        analyzer.Calls.Should().Be(120);
        analyzer.MaxConcurrency.Should().BeLessThanOrEqualTo(options.MaxConcurrentJobs * options.DocParallelismPerJob);
    }

    [Fact]
    public async Task A_crashing_job_does_not_kill_the_worker()
    {
        var options = new ProcessingOptions { MaxConcurrentJobs = 1, DocParallelismPerJob = 1 };
        var (workers, queue) = Create(options, new DocumentAnalyzer());
        var poisoned = TestData.Job(TestData.Documents("a"));
        var healthy = TestData.Job(TestData.Documents("b"));
        _events.ThrowOnStarted = job => job == poisoned;

        await workers.StartAsync(CancellationToken.None);
        try
        {
            queue.TryEnqueue(poisoned);
            queue.TryEnqueue(healthy);

            await Eventually.TrueAsync(() => healthy.Status == ProcessingStatus.Completed);
        }
        finally
        {
            await workers.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stops_cleanly()
    {
        var (workers, _) = Create(new ProcessingOptions { MaxConcurrentJobs = 4 }, new DocumentAnalyzer());

        await workers.StartAsync(CancellationToken.None);
        var stop = workers.StopAsync(CancellationToken.None);

        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));
        (finished == stop).Should().BeTrue("workers must exit promptly on shutdown");
    }
}
