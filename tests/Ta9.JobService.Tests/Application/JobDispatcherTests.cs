using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Application;

public class JobDispatcherTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ManualTimeProvider _time = new(TestJobs.T0);
    private readonly InMemoryJobRepository _jobs = new();
    private readonly InMemoryJobDocumentRepository _documents = new();
    private readonly FakeProcessorClient _processor = new();
    private readonly DispatchOptions _options = new()
    {
        MaxDispatchAttempts = 3,
        InitialBackoff = TimeSpan.FromSeconds(2),
        MaxBackoff = TimeSpan.FromSeconds(60),
        LeaseDuration = TimeSpan.FromMinutes(2),
        MaxParallelDispatches = 4
    };

    private JobDispatcher CreateDispatcher(IJobDispatchSignal? signal = null) =>
        new(_jobs, _documents, _processor, signal ?? new JobDispatchSignal(_time), Options.Create(_options), _time, NullLogger<JobDispatcher>.Instance);

    private async Task<Job> PendingJobAsync(params string[] contents)
    {
        var job = Job.Create(Guid.NewGuid(), "job", contents.Length, "corr", _time.GetUtcNow());
        await _documents.InsertManyAsync(contents.Select((c, i) => JobDocument.Create(job.Id, i, $"doc-{i}", c)).ToList(), None);
        await _jobs.InsertAsync(job, None);
        return job;
    }

    private async Task<Job> ReloadAsync(Job job) => (await _jobs.GetAsync(job.Id, None))!;

    [Fact]
    public async Task Accepted_dispatch_queues_the_job_with_all_documents_in_order()
    {
        var job = await PendingJobAsync("first", "second", "third");

        var handled = await CreateDispatcher().DispatchDueJobsAsync(None);

        handled.Should().Be(1);
        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Queued);
        var sent = _processor.Submitted.Single();
        sent.JobId.Should().Be(job.Id);
        sent.Documents.Select(d => (d.Index, d.Content)).Should().Equal((0, "first"), (1, "second"), (2, "third"));
        (await CreateDispatcher().DispatchDueJobsAsync(None)).Should().Be(0, "nothing is due any more");
    }

    [Fact]
    public async Task Busy_processor_delays_the_job_without_consuming_an_attempt()
    {
        var job = await PendingJobAsync("a");
        _processor.OnSubmit = _ => Task.FromResult(DispatchResult.Busy(TimeSpan.FromSeconds(7)));
        var dispatcher = CreateDispatcher();

        await dispatcher.DispatchDueJobsAsync(None);

        var afterBusy = await ReloadAsync(job);
        afterBusy.Status.Should().Be(JobStatus.Pending);
        afterBusy.DispatchAttempts.Should().Be(0);
        afterBusy.NextDispatchAt.Should().Be(TestJobs.T0.AddSeconds(7));
        afterBusy.LeaseUntil.Should().BeNull();

        (await dispatcher.DispatchDueJobsAsync(None)).Should().Be(0, "the retry is not due yet");

        _processor.OnSubmit = _ => Task.FromResult(DispatchResult.Accepted());
        _time.Advance(TimeSpan.FromSeconds(7));
        await dispatcher.DispatchDueJobsAsync(None);
        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task Unreachable_processor_backs_off_then_fails_the_job()
    {
        var job = await PendingJobAsync("a");
        _processor.OnSubmit = _ => Task.FromResult(DispatchResult.Unreachable("connection refused"));
        var dispatcher = CreateDispatcher();

        await dispatcher.DispatchDueJobsAsync(None);
        var afterFirst = await ReloadAsync(job);
        afterFirst.Status.Should().Be(JobStatus.Pending);
        afterFirst.DispatchAttempts.Should().Be(1);
        afterFirst.NextDispatchAt.Should().BeOnOrAfter(TestJobs.T0.AddSeconds(1.6)).And.BeOnOrBefore(TestJobs.T0.AddSeconds(2.4));

        _time.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchDueJobsAsync(None);
        (await ReloadAsync(job)).DispatchAttempts.Should().Be(2);

        _time.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchDueJobsAsync(None);
        var failed = await ReloadAsync(job);
        failed.Status.Should().Be(JobStatus.Failed);
        failed.FailureReason.Should().Contain("unreachable").And.Contain("connection refused");
        _processor.Submitted.Should().HaveCount(_options.MaxDispatchAttempts);
    }

    [Fact]
    public async Task Rejected_payload_fails_the_job_immediately()
    {
        var job = await PendingJobAsync("a");
        _processor.OnSubmit = _ => Task.FromResult(DispatchResult.Rejected("400 invalid documents"));

        await CreateDispatcher().DispatchDueJobsAsync(None);

        var failed = await ReloadAsync(job);
        failed.Status.Should().Be(JobStatus.Failed);
        failed.FailureReason.Should().Contain("400 invalid documents");
    }

    [Fact]
    public async Task Job_cancelled_while_dispatching_is_cancelled_on_the_processor()
    {
        var job = await PendingJobAsync("a");
        _processor.OnSubmit = async id =>
        {
            await _jobs.UpdateAsync(id, j => j.TryCancel(_time.GetUtcNow()), None); // user cancels mid-flight
            return DispatchResult.Accepted();
        };

        await CreateDispatcher().DispatchDueJobsAsync(None);

        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Cancelled);
        _processor.Cancelled.Should().ContainSingle().Which.Should().Be(job.Id);
    }

    [Fact]
    public async Task A_crashed_dispatch_is_retried_after_the_lease_expires()
    {
        var job = await PendingJobAsync("a");
        _processor.OnSubmit = _ => throw new InvalidOperationException("unexpected");
        var dispatcher = CreateDispatcher();

        await dispatcher.DispatchDueJobsAsync(None);
        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Pending);
        (await dispatcher.DispatchDueJobsAsync(None)).Should().Be(0, "the job is leased");

        _processor.OnSubmit = _ => Task.FromResult(DispatchResult.Accepted());
        _time.Advance(_options.LeaseDuration);
        await dispatcher.DispatchDueJobsAsync(None);
        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task Dispatches_in_bounded_parallel_batches()
    {
        _options.MaxParallelDispatches = 3;
        var jobs = new List<Job>();
        for (var i = 0; i < 10; i++)
        {
            jobs.Add(await PendingJobAsync("x"));
        }

        var current = 0;
        var max = 0;
        _processor.OnSubmit = async _ =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref max, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref current);
            return DispatchResult.Accepted();
        };

        var handled = await CreateDispatcher().DispatchDueJobsAsync(None);

        handled.Should().Be(10);
        max.Should().BeLessThanOrEqualTo(3);
        foreach (var job in jobs)
        {
            (await ReloadAsync(job)).Status.Should().Be(JobStatus.Queued);
        }
    }

    [Fact]
    public async Task Background_loop_dispatches_new_jobs_when_signalled()
    {
        var signal = new JobDispatchSignal(TimeProvider.System);
        var dispatcher = new JobDispatcher(_jobs, _documents, _processor, signal,
            Options.Create(new DispatchOptions { PollInterval = TimeSpan.FromMinutes(5) }), TimeProvider.System, NullLogger<JobDispatcher>.Instance);
        await dispatcher.StartAsync(None);
        try
        {
            var job = Job.Create(Guid.NewGuid(), "job", 1, null, DateTimeOffset.UtcNow);
            await _documents.InsertManyAsync([JobDocument.Create(job.Id, 0, "d", "c")], None);
            await _jobs.InsertAsync(job, None);
            signal.Notify();

            await Eventually.TrueAsync(async () => (await _jobs.GetAsync(job.Id, None))!.Status == JobStatus.Queued);
        }
        finally
        {
            await dispatcher.StopAsync(None);
        }
    }

    [Fact]
    public async Task Disabled_dispatcher_does_nothing()
    {
        var job = await PendingJobAsync("a");
        _options.Enabled = false;
        var dispatcher = CreateDispatcher();

        await dispatcher.StartAsync(None);
        await Task.Delay(200);
        await dispatcher.StopAsync(None);

        _processor.Submitted.Should().BeEmpty();
        (await ReloadAsync(job)).Status.Should().Be(JobStatus.Pending);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref target);
            if (value <= snapshot)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
    }
}
