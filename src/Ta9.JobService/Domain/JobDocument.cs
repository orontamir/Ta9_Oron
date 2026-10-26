using Ta9.Contracts.Processing;

namespace Ta9.JobService.Domain;

/// <summary>
/// One submitted document and, once processed, its result. Stored separately from <see cref="Job"/> so the
/// job record stays small (cheap status polling) and a 1,000-document job never hits a single-record size limit.
/// </summary>
public sealed class JobDocument
{
    /// <summary>Deterministic id ("{jobId}-{index}") so result writes are idempotent upserts by key.</summary>
    public string Id { get; set; } = string.Empty;
    public Guid JobId { get; set; }
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DocumentResultDto? Result { get; set; }

    public static string MakeId(Guid jobId, int index) => $"{jobId:N}-{index:D4}";

    public static JobDocument Create(Guid jobId, int index, string name, string content) => new()
    {
        Id = MakeId(jobId, index),
        JobId = jobId,
        Index = index,
        Name = name,
        Content = content
    };

    public JobDocument Clone() => (JobDocument)MemberwiseClone();
}
