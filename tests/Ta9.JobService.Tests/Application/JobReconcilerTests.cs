using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Application;

public class JobReconcilerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ManualTimeProvider _time = new(TestJobs.T0);
    private readonly InMemoryJobRepository _jobs = new();
    private readonly InMemoryJobDocumentRepository _documents = new();
    private readonly FakeProcessorClient _processor = new();
    private readonly RecordingDispatchSignal _signal = new();
    private readonly ReconcileOptions _options = new() { StaleAfter = TimeSpan.FromSeconds(60), MaxRedispatches = 2 };
    private readonly JobReconciler _reconciler;

    public JobReconcilerTests()
    {
        var stateUpdater = new ProcessorEventHandler(_jobs, _documents, _time, NullLogger<ProcessorEventHandler>.Instance);
        _reconciler = new JobReconciler(_jobs, _processor, stateUpdater, _signal, Options.Create(_options), _time, NullLogger<JobReconciler>.Instance);
    }

    /// <summary>A job last updated at T0; the clock is then moved past the stale threshold.</summary>
    private async Task<Job> StaleJobAsync(JobStatus status = JobStatus.Processing, int redispatchCount = 0)
    {
        var job = TestJobs.InStatus(status);
        job.RedispatchCount = redispatchCount;
        await _jobs.InsertAsync(job, None);
        await _documents.InsertManyAsync(
            Enumerable.Range(0, job.TotalDocuments).Select(i => JobDocument.Create(job.Id, i, $"doc-{i}", "c")).ToList(), None);
        _time.Advance(TimeSpan.FromMinutes(2) - (_time.GetUtcNow() - TestJobs.T0));
        return job;
    }

    private async Task<Job> ReloadAsync(Job job) => (await _jobs.GetAsync(job.Id, None))!;

    private static ProcessorLookup Remote(Job job, ProcessingStatus status, int processed = 0, bool withResults = false) =>
        new(LookupOutcome.Found, new ProcessJobStatusResponse(
            job.Id, status, job.TotalDocuments, processed, TestJobs.T0, TestJobs.T0, null,
            status == ProcessingStatus.Failed ? "All documents failed to process" : null,
            withResults ? TestJobs.Summary() : null,
            withResults ? Enumerable.Range(0, job.TotalDocuments).Select(i => TestJobs.Result(i)).ToList() : null));

    [Fact]
    public async Task Jobs_with_recent_activity_are_not_checked()
    {
        var job = TestJobs.InStatus(JobStatus.Processing);
        await _jobs.InsertAsync(job, None);
        _time.Advance(TimeSpan.FromSeconds(30));

        var report = await _reconciler.ReconcileOnceAsync(None);

        report.Checked.Should().Be(0);
        _processor.Lookups.Should().BeEmpty();
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task Only_queued_and_processing_jobs_are_reconciled(JobStatus status)
    {
        await StaleJobAsync(status);

        (await _reconciler.ReconcileOnceAsync(None)).Checked.Should().Be(0);
    }

    [Fact]
    public async Task Still_running_job_gets_fresh_progress_and_liveness()
    {
        var job = await StaleJobAsync();
        _processor.OnGet = _ => Remote(job, ProcessingStatus.Processing, processed: 2);

        var report = await _reconciler.ReconcileOnceAsync(None);

        report.StillRunning.Should().Be(1);
        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Processing);
        reloaded.ProcessedDocuments.Should().Be(2);
        reloaded.UpdatedAt.Should().Be(_time.GetUtcNow(), "so it is not re-checked on every pass");
    }

    [Fact]
    public async Task Job_waiting_in_the_processor_queue_is_only_touched()
    {
        var job = await StaleJobAsync(JobStatus.Queued);
        _processor.OnGet = _ => Remote(job, ProcessingStatus.Queued);

        await _reconciler.ReconcileOnceAsync(None);

        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Queued);
        reloaded.UpdatedAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task Lost_completion_event_is_recovered_with_results()
    {
        var job = await StaleJobAsync();
        _processor.OnGet = _ => Remote(job, ProcessingStatus.Completed, processed: job.TotalDocuments, withResults: true);

        var report = await _reconciler.ReconcileOnceAsync(None);

        report.Recovered.Should().Be(1);
        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Completed);
        (await _documents.GetResultsAsync(job.Id, 1, 10, None)).Items.Should().OnlyContain(r => r.Result != null);
    }

    [Fact]
    public async Task Lost_failure_event_is_recovered()
    {
        var job = await StaleJobAsync();
        _processor.OnGet = _ => Remote(job, ProcessingStatus.Failed);

        await _reconciler.ReconcileOnceAsync(None);

        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Failed);
        reloaded.FailureReason.Should().Contain("All documents failed");
    }

    [Fact]
    public async Task Job_lost_by_a_restarted_processor_is_redispatched()
    {
        var job = await StaleJobAsync();
        _processor.OnGet = _ => new ProcessorLookup(LookupOutcome.NotFound);

        var report = await _reconciler.ReconcileOnceAsync(None);

        report.Redispatched.Should().Be(1);
        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Pending);
        reloaded.RedispatchCount.Should().Be(1);
        reloaded.NextDispatchAt.Should().Be(_time.GetUtcNow());
        _signal.Notifications.Should().Be(1, "the dispatcher is woken immediately");
    }

    [Fact]
    public async Task Job_lost_too_many_times_fails()
    {
        var job = await StaleJobAsync(redispatchCount: _options.MaxRedispatches);
        _processor.OnGet = _ => new ProcessorLookup(LookupOutcome.NotFound);

        var report = await _reconciler.ReconcileOnceAsync(None);

        report.Failed.Should().Be(1);
        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Failed);
        reloaded.FailureReason.Should().Contain("lost the job");
    }

    [Fact]
    public async Task Unreachable_processor_leaves_the_job_alone()
    {
        var job = await StaleJobAsync();
        var before = await ReloadAsync(job);
        _processor.OnGet = _ => new ProcessorLookup(LookupOutcome.Unreachable, Error: "connection refused");

        var report = await _reconciler.ReconcileOnceAsync(None);

        report.Unreachable.Should().Be(1);
        (await ReloadAsync(job)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Event_arriving_during_reconciliation_wins_over_redispatch()
    {
        var job = await StaleJobAsync();
        _processor.OnGet = _ =>
        {
            // The WS event lands between the stale query and the recovery decision.
            _jobs.UpdateAsync(job.Id, j => j.RecordProgress(1, _time.GetUtcNow()), None).GetAwaiter().GetResult();
            return new ProcessorLookup(LookupOutcome.NotFound);
        };

        await _reconciler.ReconcileOnceAsync(None);

        var reloaded = await ReloadAsync(job);
        reloaded.Status.Should().Be(JobStatus.Processing);
        reloaded.RedispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task One_failing_job_does_not_stop_the_pass()
    {
        var broken = await StaleJobAsync();
        var healthy = TestJobs.InStatus(JobStatus.Processing);
        await _jobs.InsertAsync(healthy, None);
        _processor.OnGet = id => id == broken.Id
            ? throw new InvalidOperationException("boom")
            : Remote(healthy, ProcessingStatus.Processing, processed: 1);

        var report = await _reconciler.ReconcileOnceAsync(None);

        report.Checked.Should().Be(2);
        (await ReloadAsync(healthy)).ProcessedDocuments.Should().Be(1);
    }
}
