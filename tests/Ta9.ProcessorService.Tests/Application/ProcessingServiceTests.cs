using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Infrastructure;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Application;

public class ProcessingServiceTests
{
    private readonly InMemoryProcessingJobStore _store = new();
    private readonly RecordingJobEventPublisher _events = new();
    private readonly ProcessingService _service;

    public ProcessingServiceTests()
    {
        var queue = new ChannelJobQueue(Options.Create(new ProcessingOptions { QueueCapacity = 1 }));
        _service = new ProcessingService(_store, queue, _events, TimeProvider.System, NullLogger<ProcessingService>.Instance);
    }

    private static ProcessJobRequest Request() => new(Guid.NewGuid(), TestData.Documents("a b"));

    [Fact]
    public void Accepts_and_stores_a_new_job()
    {
        var request = Request();

        var result = _service.Submit(request);

        result.Outcome.Should().Be(SubmitOutcome.Accepted);
        result.Job!.Status.Should().Be(ProcessingStatus.Queued);
        _service.Get(request.JobId).Should().BeSameAs(result.Job);
    }

    [Fact]
    public void Resubmitting_the_same_job_id_is_idempotent()
    {
        var request = Request();
        var first = _service.Submit(request);

        var second = _service.Submit(request);

        second.Outcome.Should().Be(SubmitOutcome.AlreadyExists);
        second.Job.Should().BeSameAs(first.Job);
    }

    [Fact]
    public void Rejects_when_queue_is_full_and_forgets_the_job()
    {
        _service.Submit(Request());
        var overflow = Request();

        var result = _service.Submit(overflow);

        result.Outcome.Should().Be(SubmitOutcome.QueueFull);
        _service.Get(overflow.JobId).Should().BeNull("a rejected job must be retryable later");
    }

    [Fact]
    public void Cancel_unknown_job_returns_not_found() =>
        _service.Cancel(Guid.NewGuid()).Outcome.Should().Be(CancelOutcome.NotFound);

    [Fact]
    public void Cancel_publishes_once_and_is_idempotent()
    {
        var request = Request();
        _service.Submit(request);

        _service.Cancel(request.JobId).Outcome.Should().Be(CancelOutcome.Cancelled);
        _service.Cancel(request.JobId).Outcome.Should().Be(CancelOutcome.Cancelled);

        _events.TypesFor(request.JobId).Should().Equal("JobCancelled");
    }

    [Fact]
    public void Cancel_finished_job_is_rejected()
    {
        var request = Request();
        var job = _service.Submit(request).Job!;
        job.TryStart(DateTimeOffset.UtcNow);
        job.TryFail("boom", DateTimeOffset.UtcNow);

        _service.Cancel(request.JobId).Outcome.Should().Be(CancelOutcome.AlreadyFinished);
    }
}
