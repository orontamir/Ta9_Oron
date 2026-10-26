namespace Ta9.JobService.Domain;

/// <summary>
/// The single source of truth for legal job status transitions.
/// Anything not listed here is rejected: duplicates, out-of-order events and late events after a terminal
/// state therefore become harmless no-ops.
/// </summary>
public static class JobStateMachine
{
    private static readonly IReadOnlyDictionary<JobStatus, JobStatus[]> Allowed = new Dictionary<JobStatus, JobStatus[]>
    {
        [JobStatus.Pending] = [JobStatus.Queued, JobStatus.Failed, JobStatus.Cancelled],
        // Queued → Completed covers a lost JobStarted; → Pending is the reconciler recovering a lost job.
        [JobStatus.Queued] = [JobStatus.Processing, JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Pending],
        [JobStatus.Processing] = [JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Pending],
        [JobStatus.Completed] = [],
        [JobStatus.Failed] = [],
        [JobStatus.Cancelled] = []
    };

    public static bool CanTransition(JobStatus from, JobStatus to) => Allowed[from].Contains(to);

    public static bool IsTerminal(JobStatus status) => Allowed[status].Length == 0;

    /// <summary>Active jobs still own work somewhere in the system (cannot be deleted).</summary>
    public static bool IsActive(JobStatus status) => !IsTerminal(status);
}
