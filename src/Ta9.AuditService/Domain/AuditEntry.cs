using Ta9.Contracts.Audit;

namespace Ta9.AuditService.Domain;

/// <summary>Immutable summary of a finished job. The first write wins; later writes for the same job are ignored.</summary>
public sealed class AuditEntry
{
    public Guid JobId { get; set; }
    public string? Name { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalDocuments { get; set; }
    public int SucceededDocuments { get; set; }
    public int FailedDocuments { get; set; }
    public long TotalWords { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    public static AuditEntry From(JobAuditRequest request, DateTimeOffset recordedAt) => new()
    {
        JobId = request.JobId,
        Name = request.Name,
        Status = request.Status,
        TotalDocuments = request.TotalDocuments,
        SucceededDocuments = request.SucceededDocuments,
        FailedDocuments = request.FailedDocuments,
        TotalWords = request.TotalWords,
        FailureReason = request.FailureReason,
        CreatedAt = request.CreatedAt,
        CompletedAt = request.CompletedAt,
        RecordedAt = recordedAt
    };

    public JobAuditResponse ToResponse() => new(
        JobId, Name, Status, TotalDocuments, SucceededDocuments, FailedDocuments, TotalWords,
        FailureReason, CreatedAt, CompletedAt, RecordedAt);

    public AuditEntry Clone() => (AuditEntry)MemberwiseClone();
}
