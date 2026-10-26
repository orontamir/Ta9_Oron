using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Ta9.Contracts.Events;
using Ta9.Contracts.Processing;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Application;

public class ProcessorEventHandlerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ManualTimeProvider _time = new(TestJobs.T0);
    private readonly InMemoryJobRepository _jobs = new();
    private readonly InMemoryJobDocumentRepository _documents = new();
    private readonly ProcessorEventHandler _handler;

    public ProcessorEventHandlerTests() =>
        _handler = new ProcessorEventHandler(_jobs, _documents, _time, NullLogger<ProcessorEventHandler>.Instance);

    internal static ProcessorEventEnvelope Event<TPayload>(string type, Guid jobId, TPayload payload) =>
        ProcessorEventSerializer.Create(type, jobId, payload, DateTimeOffset.UtcNow, "corr-ws");

    internal static ProcessorEventEnvelope Started(Guid jobId) => Event(ProcessorEventTypes.JobStarted, jobId, new JobStartedPayload(3));

    internal static ProcessorEventEnvelope Completed(Guid jobId, int documents = 3) =>
        Event(ProcessorEventTypes.JobCompleted, jobId, new JobCompletedPayload(
            TestJobs.Summary(),
            Enumerable.Range(0, documents).Select(i => TestJobs.Result(i, $"doc-{i}")).ToList()));

    private async Task<Job> JobAsync(JobStatus status, int documents = 3)
    {
        var job = TestJobs.InStatus(status);
        await _jobs.InsertAsync(job, None);
        await _documents.InsertManyAsync(
            Enumerable.Range(0, documents).Select(i => JobDocument.Create(job.Id, i, $"doc-{i}", "content")).ToList(), None);
        return job;
    }

    private async Task<Job> ReloadAsync(Job job) => (await _jobs.GetAsync(job.Id, None))!;

    [Fact]
    public async Task Started_moves_a_queued_job_to_processing()
    {
        var job = await JobAsync(JobStatus.Queued);

        (await _handler.HandleAsync(Started(job.Id), None)).Should().Be(EventHandlingOutcome.Applied);

        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Processing);
    }

    [Fact]
    public async Task Events_arriving_before_the_dispatcher_recorded_the_202_still_apply()
    {
        // Race: a tiny job finishes on the processor before the dispatcher marks it Queued.
        var job = await JobAsync(JobStatus.Pending);
        job.LeaseUntil.Should().BeNull();

        await _handler.HandleAsync(Started(job.Id), None);
        await _handler.HandleAsync(Completed(job.Id), None);

        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Completed);
        reloaded.NextDispatchAt.Should().BeNull("the job must not be dispatched again");
    }

    [Fact]
    public async Task Progress_is_recorded_and_never_goes_backwards()
    {
        var job = await JobAsync(JobStatus.Processing);

        await _handler.HandleAsync(Event(ProcessorEventTypes.JobProgress, job.Id, new JobProgressPayload(2, 3)), None);
        await _handler.HandleAsync(Event(ProcessorEventTypes.JobProgress, job.Id, new JobProgressPayload(1, 3)), None);

        (await ReloadAsync(job)).ProcessedDocuments.Should().Be(2);
    }

    [Fact]
    public async Task Completed_stores_results_then_completes_the_job()
    {
        var job = await JobAsync(JobStatus.Processing);

        (await _handler.HandleAsync(Completed(job.Id), None)).Should().Be(EventHandlingOutcome.Applied);

        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Completed);
        reloaded.Summary.Should().Be(TestJobs.Summary());
        var results = await _documents.GetResultsAsync(job.Id, 1, 10, None);
        results.Items.Should().OnlyContain(r => r.Result != null && r.Result.WordCount == 3);
    }

    [Fact]
    public async Task Replayed_completion_is_ignored()
    {
        var job = await JobAsync(JobStatus.Processing);
        var completed = Completed(job.Id);
        await _handler.HandleAsync(completed, None);
        var versionAfterFirst = (await ReloadAsync(job)).Version;

        (await _handler.HandleAsync(completed, None)).Should().Be(EventHandlingOutcome.Ignored);

        (await ReloadAsync(job)).Version.Should().Be(versionAfterFirst);
    }

    [Fact]
    public async Task Late_completion_of_a_cancelled_job_is_ignored_and_stores_no_results()
    {
        var job = await JobAsync(JobStatus.Cancelled);

        (await _handler.HandleAsync(Completed(job.Id), None)).Should().Be(EventHandlingOutcome.Ignored);

        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Cancelled);
        (await _documents.GetResultsAsync(job.Id, 1, 10, None)).Items.Should().OnlyContain(r => r.Result == null);
    }

    [Fact]
    public async Task Out_of_order_started_after_completed_is_ignored()
    {
        var job = await JobAsync(JobStatus.Completed);

        (await _handler.HandleAsync(Started(job.Id), None)).Should().Be(EventHandlingOutcome.Ignored);

        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task Failed_records_code_and_reason()
    {
        var job = await JobAsync(JobStatus.Processing);

        await _handler.HandleAsync(Event(ProcessorEventTypes.JobFailed, job.Id, new JobFailedPayload("ProcessingError", "All documents failed")), None);

        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Failed);
        reloaded.FailureReason.Should().Be("ProcessingError: All documents failed");
    }

    [Fact]
    public async Task Unknown_job_is_ignored() =>
        (await _handler.HandleAsync(Started(Guid.NewGuid()), None)).Should().Be(EventHandlingOutcome.Ignored);

    [Fact]
    public async Task Unknown_event_type_is_ignored()
    {
        var job = await JobAsync(JobStatus.Queued);

        (await _handler.HandleAsync(Event("JobTeleported", job.Id, new { }), None)).Should().Be(EventHandlingOutcome.Ignored);
        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task Newer_schema_version_is_ignored()
    {
        var job = await JobAsync(JobStatus.Queued);

        (await _handler.HandleAsync(Started(job.Id) with { SchemaVersion = 99 }, None)).Should().Be(EventHandlingOutcome.Ignored);
        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task Malformed_payload_is_ignored_not_retried()
    {
        var job = await JobAsync(JobStatus.Processing);
        var broken = Event(ProcessorEventTypes.JobCompleted, job.Id, new { summary = "not an object" });

        (await _handler.HandleAsync(broken, None)).Should().Be(EventHandlingOutcome.Ignored);
    }

    [Fact]
    public async Task Storage_failure_propagates_so_the_event_is_not_acknowledged()
    {
        var handler = new ProcessorEventHandler(new ThrowingJobRepository(), _documents, _time, NullLogger<ProcessorEventHandler>.Instance);

        var act = () => handler.HandleAsync(Started(Guid.NewGuid()), None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    internal sealed class ThrowingJobRepository : IJobRepository
    {
        private static Exception Down() => new TimeoutException("storage down");

        public Task InsertAsync(Job job, CancellationToken ct) => throw Down();
        public Task<Job?> GetAsync(Guid id, CancellationToken ct) => throw Down();
        public Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken ct) => throw Down();
        public Task<bool> TryUpdateAsync(Job job, CancellationToken ct) => throw Down();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw Down();
        public Task<Job?> TryClaimNextForDispatchAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct) => throw Down();
    }
}
