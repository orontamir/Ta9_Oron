using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Domain;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Application;

public class JobRunnerTests
{
    private readonly RecordingJobEventPublisher _events = new();
    private readonly ManualTimeProvider _time = new();

    private JobRunner CreateRunner(IDocumentAnalyzer? analyzer = null, int docParallelism = 2) =>
        new(analyzer ?? new DocumentAnalyzer(),
            _events,
            Options.Create(new ProcessingOptions { DocParallelismPerJob = docParallelism }),
            _time,
            NullLogger<JobRunner>.Instance);

    [Fact]
    public async Task Processes_all_documents_and_completes()
    {
        var job = TestData.Job(TestData.Documents("a b", "c c c", ""), _time);

        await CreateRunner().RunAsync(job, CancellationToken.None);

        job.Status.Should().Be(ProcessingStatus.Completed);
        job.ProcessedDocuments.Should().Be(3);
        job.Results!.Select(r => r.Index).Should().Equal(0, 1, 2);
        job.Results!.Select(r => r.WordCount).Should().Equal(2, 3, 0);
        job.Results![1].TopWords.Should().Equal(new WordCountDto("c", 3));
        job.Summary.Should().BeEquivalentTo(new { TotalDocuments = 3, Succeeded = 3, Failed = 0, TotalWords = 5L });
        _events.TypesFor(job.JobId).Should().Equal("JobStarted", "JobCompleted");
    }

    [Fact]
    public async Task Isolates_a_failing_document()
    {
        var analyzer = new DelegateAnalyzer(content =>
            content == "boom" ? throw new InvalidOperationException("bad doc") : DelegateAnalyzer.Real.Analyze(content));
        var job = TestData.Job(TestData.Documents("ok one", "boom", "ok two"), _time);

        await CreateRunner(analyzer).RunAsync(job, CancellationToken.None);

        job.Status.Should().Be(ProcessingStatus.Completed);
        job.Summary!.Failed.Should().Be(1);
        job.Summary.Succeeded.Should().Be(2);
        var failed = job.Results!.Single(r => r.Status == DocumentStatus.Failed);
        failed.Index.Should().Be(1);
        failed.Error.Should().Contain(nameof(InvalidOperationException));
        failed.Sha256.Should().BeNull();
        _events.TypesFor(job.JobId).Should().EndWith("JobCompleted");
    }

    [Fact]
    public async Task Fails_the_job_when_every_document_fails()
    {
        var analyzer = new DelegateAnalyzer(_ => throw new InvalidOperationException());
        var job = TestData.Job(TestData.Documents("a", "b"), _time);

        await CreateRunner(analyzer).RunAsync(job, CancellationToken.None);

        job.Status.Should().Be(ProcessingStatus.Failed);
        job.FailureReason.Should().NotBeNullOrWhiteSpace();
        _events.TypesFor(job.JobId).Should().Equal("JobStarted", "JobFailed");
    }

    [Fact]
    public async Task Skips_a_job_cancelled_while_queued()
    {
        var analyzer = new ConcurrencyTrackingAnalyzer(TimeSpan.Zero);
        var job = TestData.Job(TestData.Documents("a"), _time);
        job.TryCancel(_time.GetUtcNow());

        await CreateRunner(analyzer).RunAsync(job, CancellationToken.None);

        analyzer.Calls.Should().Be(0);
        _events.TypesFor(job.JobId).Should().BeEmpty();
    }

    [Fact]
    public async Task Stops_when_cancelled_mid_run_without_reporting_completion()
    {
        ProcessingJob job = null!;
        var analyzer = new DelegateAnalyzer(content =>
        {
            if (content == "cancel-me")
            {
                job.TryCancel(DateTimeOffset.UtcNow);
            }
            return DelegateAnalyzer.Real.Analyze(content);
        });
        job = TestData.Job(TestData.Documents("a", "cancel-me", "c", "d", "e"), _time);

        await CreateRunner(analyzer, docParallelism: 1).RunAsync(job, CancellationToken.None);

        job.Status.Should().Be(ProcessingStatus.Cancelled);
        job.ProcessedDocuments.Should().BeLessThan(5);
        _events.TypesFor(job.JobId).Should().NotContain("JobCompleted");
    }

    [Fact]
    public async Task Abandons_job_silently_on_shutdown()
    {
        using var shutdown = new CancellationTokenSource();
        var analyzer = new DelegateAnalyzer(content =>
        {
            shutdown.Cancel();
            return DelegateAnalyzer.Real.Analyze(content);
        });
        var job = TestData.Job(TestData.Documents("a", "b", "c"), _time);

        await CreateRunner(analyzer, docParallelism: 1).RunAsync(job, shutdown.Token);

        job.Status.Should().Be(ProcessingStatus.Processing, "the Job Service reconciler recovers abandoned jobs");
        _events.TypesFor(job.JobId).Should().Equal("JobStarted");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Never_exceeds_document_parallelism(int parallelism)
    {
        var analyzer = new ConcurrencyTrackingAnalyzer(TimeSpan.FromMilliseconds(15));
        var job = TestData.Job(TestData.Documents(24), _time);

        await CreateRunner(analyzer, parallelism).RunAsync(job, CancellationToken.None);

        job.Status.Should().Be(ProcessingStatus.Completed);
        analyzer.Calls.Should().Be(24);
        analyzer.MaxConcurrency.Should().BeLessThanOrEqualTo(parallelism);
    }

    [Fact]
    public async Task Publishes_throttled_progress()
    {
        var job = TestData.Job(TestData.Documents(100), _time);

        await CreateRunner(docParallelism: 1).RunAsync(job, CancellationToken.None);

        var types = _events.TypesFor(job.JobId);
        types.Count(t => t == "JobProgress").Should().Be(9);
        types.First().Should().Be("JobStarted");
        types.Last().Should().Be("JobCompleted");
    }
}
