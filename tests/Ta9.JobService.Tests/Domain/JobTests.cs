using Ta9.JobService.Domain;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Domain;

public class JobTests
{
    private static readonly DateTimeOffset T1 = TestJobs.T0.AddSeconds(10);

    [Fact]
    public void New_job_is_pending_and_due_for_dispatch()
    {
        var job = TestJobs.New();

        job.Status.Should().Be(JobStatus.Pending);
        job.NextDispatchAt.Should().Be(TestJobs.T0);
        job.Version.Should().Be(0);
        job.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void Marking_queued_clears_dispatch_scheduling()
    {
        var job = TestJobs.New();
        job.LeaseUntil = TestJobs.T0.AddSeconds(30);

        job.TryMarkQueued(T1).Should().BeTrue();

        job.Status.Should().Be(JobStatus.Queued);
        job.LeaseUntil.Should().BeNull();
        job.NextDispatchAt.Should().BeNull();
        job.UpdatedAt.Should().Be(T1);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Dispatch_retry_counts_only_real_failures(bool countAttempt, int expectedAttempts)
    {
        var job = TestJobs.New();

        job.ScheduleDispatchRetry(T1, countAttempt, TestJobs.T0).Should().BeTrue();

        job.DispatchAttempts.Should().Be(expectedAttempts);
        job.NextDispatchAt.Should().Be(T1);
    }

    [Fact]
    public void Dispatch_retry_is_ignored_once_the_job_left_pending()
    {
        var job = TestJobs.InStatus(JobStatus.Queued);

        job.ScheduleDispatchRetry(T1, countAttempt: true, T1).Should().BeFalse();
        job.DispatchAttempts.Should().Be(0);
    }

    [Fact]
    public void Progress_on_a_queued_job_starts_it()
    {
        var job = TestJobs.InStatus(JobStatus.Queued);

        job.RecordProgress(1, T1).Should().BeTrue();

        job.Status.Should().Be(JobStatus.Processing);
        job.StartedAt.Should().Be(T1);
        job.ProcessedDocuments.Should().Be(1);
    }

    [Fact]
    public void Progress_never_moves_backwards_or_beyond_total()
    {
        var job = TestJobs.InStatus(JobStatus.Processing);

        job.RecordProgress(2, T1);
        job.RecordProgress(1, T1);
        job.ProcessedDocuments.Should().Be(2);

        job.RecordProgress(99, T1);
        job.ProcessedDocuments.Should().Be(job.TotalDocuments);
    }

    [Fact]
    public void Progress_after_a_terminal_state_is_ignored()
    {
        var job = TestJobs.InStatus(JobStatus.Cancelled);

        job.RecordProgress(2, T1).Should().BeFalse();
        job.ProcessedDocuments.Should().Be(0);
    }

    [Fact]
    public void Completion_without_a_started_event_is_accepted()
    {
        var job = TestJobs.InStatus(JobStatus.Queued);

        job.TryComplete(TestJobs.Summary(), T1).Should().BeTrue();

        job.Status.Should().Be(JobStatus.Completed);
        job.StartedAt.Should().Be(T1);
        job.CompletedAt.Should().Be(T1);
        job.ProcessedDocuments.Should().Be(job.TotalDocuments);
        job.Summary.Should().Be(TestJobs.Summary());
    }

    [Fact]
    public void Late_completion_after_cancel_is_rejected()
    {
        var job = TestJobs.InStatus(JobStatus.Processing);
        job.TryCancel(T1);

        job.TryComplete(TestJobs.Summary(), T1).Should().BeFalse();

        job.Status.Should().Be(JobStatus.Cancelled);
        job.Summary.Should().BeNull();
    }

    [Fact]
    public void Failing_records_the_reason()
    {
        var job = TestJobs.InStatus(JobStatus.Pending);

        job.TryFail("Processor unreachable", T1).Should().BeTrue();

        job.FailureReason.Should().Be("Processor unreachable");
        job.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Returning_to_pending_resets_progress_and_counts_the_redispatch()
    {
        var job = TestJobs.InStatus(JobStatus.Processing);
        job.RecordProgress(2, TestJobs.T0);
        job.DispatchAttempts = 2;

        job.TryReturnToPending(T1).Should().BeTrue();

        job.Status.Should().Be(JobStatus.Pending);
        job.RedispatchCount.Should().Be(1);
        job.DispatchAttempts.Should().Be(0);
        job.ProcessedDocuments.Should().Be(0);
        job.StartedAt.Should().BeNull();
        job.NextDispatchAt.Should().Be(T1);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var job = TestJobs.New();

        var copy = job.Clone();
        copy.TryCancel(T1);

        job.Status.Should().Be(JobStatus.Pending);
    }
}
