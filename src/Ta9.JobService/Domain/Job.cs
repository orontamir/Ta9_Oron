using Ta9.Contracts.Processing;

namespace Ta9.JobService.Domain;

/// <summary>
/// Job aggregate root. State changes go through the Try* methods, which consult <see cref="JobStateMachine"/>
/// and return false for illegal transitions. Properties have public setters only so the persistence layer
/// can map the class without attributes; application code must use the methods.
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public JobStatus Status { get; set; }
    public int TotalDocuments { get; set; }
    public int ProcessedDocuments { get; set; }
    public ProcessingSummaryDto? Summary { get; set; }
    public string? FailureReason { get; set; }
    public string? CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Last change of any kind, including progress. Used by the reconciler to detect stale jobs.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Dispatch bookkeeping (see Dispatcher / Reconciler).
    public int DispatchAttempts { get; set; }
    public DateTimeOffset? NextDispatchAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int RedispatchCount { get; set; }

    /// <summary>Outbox flag: terminal summary not yet delivered to the Audit Service.</summary>
    public bool AuditPublished { get; set; }

    /// <summary>Optimistic concurrency token, incremented by the repository on every successful update.</summary>
    public long Version { get; set; }

    public static Job Create(Guid id, string? name, int totalDocuments, string? correlationId, DateTimeOffset now) => new()
    {
        Id = id,
        Name = name,
        Status = JobStatus.Pending,
        TotalDocuments = totalDocuments,
        CorrelationId = correlationId,
        CreatedAt = now,
        UpdatedAt = now,
        NextDispatchAt = now
    };

    /// <summary>The Processor accepted the job.</summary>
    public bool TryMarkQueued(DateTimeOffset now) => TryTransition(JobStatus.Queued, now, () =>
    {
        LeaseUntil = null;
        NextDispatchAt = null;
    });

    /// <summary>Dispatch did not succeed; stay Pending and try again at <paramref name="nextAttemptAt"/>.</summary>
    /// <param name="countAttempt">False for backpressure (503): it is not a failure of the processor.</param>
    public bool ScheduleDispatchRetry(DateTimeOffset nextAttemptAt, bool countAttempt, DateTimeOffset now)
    {
        if (Status != JobStatus.Pending)
        {
            return false;
        }

        if (countAttempt)
        {
            DispatchAttempts++;
        }

        NextDispatchAt = nextAttemptAt;
        LeaseUntil = null;
        UpdatedAt = now;
        return true;
    }

    public bool TryStart(DateTimeOffset now) => TryTransition(JobStatus.Processing, now, () => StartedAt ??= now);

    /// <summary>Progress is monotonic: out-of-order or duplicate reports never move it backwards.</summary>
    public bool RecordProgress(int processedDocuments, DateTimeOffset now)
    {
        if (Status == JobStatus.Queued)
        {
            TryStart(now); // progress implies the job has started (JobStarted may have been lost)
        }

        if (Status != JobStatus.Processing)
        {
            return false;
        }

        ProcessedDocuments = Math.Max(ProcessedDocuments, Math.Min(processedDocuments, TotalDocuments));
        UpdatedAt = now;
        return true;
    }

    public bool TryComplete(ProcessingSummaryDto summary, DateTimeOffset now) => TryTransition(JobStatus.Completed, now, () =>
    {
        Summary = summary;
        ProcessedDocuments = TotalDocuments;
        StartedAt ??= now;
        CompletedAt = now;
    });

    public bool TryFail(string reason, DateTimeOffset now) => TryTransition(JobStatus.Failed, now, () =>
    {
        FailureReason = reason;
        CompletedAt = now;
        LeaseUntil = null;
        NextDispatchAt = null;
    });

    public bool TryCancel(DateTimeOffset now) => TryTransition(JobStatus.Cancelled, now, () =>
    {
        CompletedAt = now;
        LeaseUntil = null;
        NextDispatchAt = null;
    });

    /// <summary>The Processor lost the job (e.g. restarted): dispatch it again from scratch.</summary>
    public bool TryReturnToPending(DateTimeOffset now) => TryTransition(JobStatus.Pending, now, () =>
    {
        RedispatchCount++;
        DispatchAttempts = 0;
        ProcessedDocuments = 0;
        StartedAt = null;
        NextDispatchAt = now;
        LeaseUntil = null;
    });

    /// <summary>Records that the job is known to be alive (used by the reconciler) without changing its state.</summary>
    public bool Touch(DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return false;
        }

        UpdatedAt = now;
        return true;
    }

    public bool IsTerminal => JobStateMachine.IsTerminal(Status);

    /// <summary>Shallow copy; all members are values or immutable records.</summary>
    public Job Clone() => (Job)MemberwiseClone();

    private bool TryTransition(JobStatus to, DateTimeOffset now, Action apply)
    {
        if (!JobStateMachine.CanTransition(Status, to))
        {
            return false;
        }

        Status = to;
        apply();
        UpdatedAt = now;
        return true;
    }
}
