namespace Ta9.Contracts.Audit;

// Contracts of the Audit Service REST API (/api/audit), shared by the Audit Service and its client (Job Service).
// Status is a string on purpose: the audit log must not depend on the Job Service's internal enum.

public static class AuditJobStatuses
{
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static bool IsTerminal(string? status) => status is Completed or Failed or Cancelled;
}

public sealed record JobAuditRequest(
    Guid JobId,
    string? Name,
    string Status,
    int TotalDocuments,
    int SucceededDocuments,
    int FailedDocuments,
    long TotalWords,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt);

public sealed record JobAuditResponse(
    Guid JobId,
    string? Name,
    string Status,
    int TotalDocuments,
    int SucceededDocuments,
    int FailedDocuments,
    long TotalWords,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt,
    DateTimeOffset RecordedAt);
