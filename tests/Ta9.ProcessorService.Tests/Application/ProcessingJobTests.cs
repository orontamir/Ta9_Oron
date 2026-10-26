using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Application;

public class ProcessingJobTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly ProcessingSummaryDto Summary = new(1, 1, 0, 3, 10);

    [Fact]
    public void New_job_is_queued_with_documents()
    {
        var job = TestData.Job(TestData.Documents("a b c"));

        job.Status.Should().Be(ProcessingStatus.Queued);
        job.TotalDocuments.Should().Be(1);
        job.Documents.Should().HaveCount(1);
        job.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void Starts_only_once()
    {
        var job = TestData.Job(TestData.Documents("a"));

        job.TryStart(Now).Should().BeTrue();
        job.TryStart(Now).Should().BeFalse();
        job.Status.Should().Be(ProcessingStatus.Processing);
    }

    [Fact]
    public void Completes_only_from_processing_and_releases_documents()
    {
        var job = TestData.Job(TestData.Documents("a"));
        job.TryComplete([], Summary, Now).Should().BeFalse("a queued job cannot complete");

        job.TryStart(Now);
        job.TryComplete([], Summary, Now).Should().BeTrue();

        job.Status.Should().Be(ProcessingStatus.Completed);
        job.Summary.Should().Be(Summary);
        job.Documents.Should().BeEmpty("content is no longer needed once the job is finished");
        job.TotalDocuments.Should().Be(1);
    }

    [Fact]
    public void Cancel_moves_to_cancelled_and_signals_the_token()
    {
        var job = TestData.Job(TestData.Documents("a"));

        job.TryCancel(Now).Should().BeTrue();

        job.Status.Should().Be(ProcessingStatus.Cancelled);
        job.CancellationToken.IsCancellationRequested.Should().BeTrue();
        job.TryStart(Now).Should().BeFalse("a cancelled job must not start");
    }

    [Fact]
    public void Completion_after_cancel_is_ignored()
    {
        var job = TestData.Job(TestData.Documents("a"));
        job.TryStart(Now);
        job.TryCancel(Now);

        job.TryComplete([], Summary, Now).Should().BeFalse();
        job.Status.Should().Be(ProcessingStatus.Cancelled);
    }

    [Fact]
    public void Terminal_jobs_cannot_be_failed_or_cancelled()
    {
        var job = TestData.Job(TestData.Documents("a"));
        job.TryStart(Now);
        job.TryComplete([], Summary, Now);

        job.TryFail("late", Now).Should().BeFalse();
        job.TryCancel(Now).Should().BeFalse();
        job.Status.Should().Be(ProcessingStatus.Completed);
    }

    [Fact]
    public void Response_includes_results_only_when_asked()
    {
        var job = TestData.Job(TestData.Documents("a"));
        job.TryStart(Now);
        job.TryComplete([], Summary, Now);

        job.ToResponse(includeResults: true).Results.Should().NotBeNull();
        job.ToResponse(includeResults: false).Results.Should().BeNull();
    }
}
