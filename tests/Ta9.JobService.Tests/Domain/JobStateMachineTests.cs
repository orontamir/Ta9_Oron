using Ta9.JobService.Domain;

namespace Ta9.JobService.Tests.Domain;

public class JobStateMachineTests
{
    // The designed lifecycle (docs/design/01-architecture.md §3), written independently of the implementation.
    private static readonly Dictionary<JobStatus, JobStatus[]> Expected = new()
    {
        [JobStatus.Pending] = [JobStatus.Queued, JobStatus.Failed, JobStatus.Cancelled],
        [JobStatus.Queued] = [JobStatus.Processing, JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Pending],
        [JobStatus.Processing] = [JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Pending],
        [JobStatus.Completed] = [],
        [JobStatus.Failed] = [],
        [JobStatus.Cancelled] = []
    };

    public static IEnumerable<object[]> AllPairs() =>
        from source in Enum.GetValues<JobStatus>()
        from target in Enum.GetValues<JobStatus>()
        select new object[] { source, target };

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Every_transition_matches_the_designed_table(JobStatus source, JobStatus target) =>
        JobStateMachine.CanTransition(source, target).Should().Be(Expected[source].Contains(target));

    [Theory]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Cancelled, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Queued, false)]
    [InlineData(JobStatus.Processing, false)]
    public void Terminal_states_are_completed_failed_and_cancelled(JobStatus status, bool terminal)
    {
        JobStateMachine.IsTerminal(status).Should().Be(terminal);
        JobStateMachine.IsActive(status).Should().Be(!terminal);
    }
}
