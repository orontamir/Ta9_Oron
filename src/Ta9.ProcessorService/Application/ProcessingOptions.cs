using System.ComponentModel.DataAnnotations;

namespace Ta9.ProcessorService.Application;

public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>Number of jobs processed at the same time (worker loops).</summary>
    [Range(1, 256)]
    public int MaxConcurrentJobs { get; set; } = 10;

    /// <summary>Jobs accepted but waiting for a free worker. When full, new jobs get 503.</summary>
    [Range(1, 100_000)]
    public int QueueCapacity { get; set; } = 100;

    /// <summary>Documents of a single job analyzed in parallel. Total CPU work ≤ MaxConcurrentJobs × this.</summary>
    [Range(1, 64)]
    public int DocParallelismPerJob { get; set; } = 2;

    /// <summary>How long finished jobs (and their results) stay queryable for reconciliation.</summary>
    public TimeSpan ResultsTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Retry-After hint returned with 503 when the queue is full.</summary>
    [Range(1, 3600)]
    public int RetryAfterSeconds { get; set; } = 5;
}
