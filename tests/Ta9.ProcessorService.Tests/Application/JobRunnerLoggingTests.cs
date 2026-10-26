using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Domain;
using Ta9.ProcessorService.Tests.TestSupport;
using Ta9.Tests.Shared;

namespace Ta9.ProcessorService.Tests.Application;

/// <summary>What processing a job logs: flow at Information, progress at Debug, a bad document at Warning, a failed job at Error.</summary>
public class JobRunnerLoggingTests
{
    private readonly RecordingJobEventPublisher _events = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ListLogger<JobRunner> _logger = new();

    private JobRunner CreateRunner(IDocumentAnalyzer? analyzer = null) =>
        new(analyzer ?? new DocumentAnalyzer(), _events, Options.Create(new ProcessingOptions { DocParallelismPerJob = 2 }), _time, _logger);

    [Fact]
    public async Task Successful_job_logs_start_and_completion_with_the_job_id()
    {
        var job = TestData.Job(TestData.Documents(20), _time);

        await CreateRunner().RunAsync(job, CancellationToken.None);

        var flow = _logger.AtLevel(LogLevel.Information).Select(e => e.Message).ToList();
        flow.Should().HaveCount(2);
        flow[0].Should().Be("Job started with 20 documents");
        flow[1].Should().StartWith("Job completed: 20 succeeded, 0 failed in ");
        _logger.AtLevel(LogLevel.Debug).Should().Contain(e => e.Message.StartsWith("Progress "), "progress is a Debug detail");
        _logger.Entries.Should().OnlyContain(e => Equals(e.Scope.GetValueOrDefault("JobId"), job.JobId));
        _logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Failing_document_is_a_warning_with_the_exception_and_the_job_still_completes()
    {
        var analyzer = new DelegateAnalyzer(content =>
            content == "boom" ? throw new InvalidOperationException("bad doc") : DelegateAnalyzer.Real.Analyze(content));
        var job = TestData.Job(TestData.Documents("ok", "boom"), _time);

        await CreateRunner(analyzer).RunAsync(job, CancellationToken.None);

        var warning = _logger.AtLevel(LogLevel.Warning).Should().ContainSingle().Which;
        warning.Message.Should().Be("Document 1 (doc-1.txt) failed");
        warning.Exception!.Message.Should().Be("bad doc");
        _logger.AtLevel(LogLevel.Error).Should().BeEmpty();
        _logger.AtLevel(LogLevel.Information).Should().Contain(e => e.Message.StartsWith("Job completed: 1 succeeded, 1 failed"));
    }

    [Fact]
    public async Task Job_where_every_document_fails_is_logged_as_an_error()
    {
        var job = TestData.Job(TestData.Documents("a", "b"), _time);

        await CreateRunner(new DelegateAnalyzer(_ => throw new InvalidOperationException())).RunAsync(job, CancellationToken.None);

        _logger.AtLevel(LogLevel.Error).Should().ContainSingle(e => e.Message == "Job failed: all 2 documents failed");
    }
}
