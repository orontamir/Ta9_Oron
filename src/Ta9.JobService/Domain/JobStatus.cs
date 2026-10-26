namespace Ta9.JobService.Domain;

public enum JobStatus
{
    /// <summary>Persisted, waiting to be dispatched to the Processor.</summary>
    Pending,

    /// <summary>Accepted by the Processor, waiting for a free worker.</summary>
    Queued,

    Processing,
    Completed,
    Failed,
    Cancelled
}
