using System.Text;
using Ta9.AuditService.Domain;
using Ta9.Contracts;
using Ta9.Contracts.Audit;

namespace Ta9.AuditService.Application;

public interface IAuditRepository
{
    /// <summary>Inserts the entry unless one already exists for the job. Returns false when it already existed.</summary>
    Task<bool> TryInsertAsync(AuditEntry entry, CancellationToken cancellationToken);

    Task<AuditEntry?> GetAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed record RecordResult(bool Created, AuditEntry Entry);

/// <summary>Use-cases behind /api/audit.</summary>
public sealed class AuditLog(IAuditRepository repository, TimeProvider time, ILogger<AuditLog> logger)
{
    /// <summary>Idempotent: recording the same job twice returns the original entry unchanged.</summary>
    public async Task<RecordResult> RecordAsync(JobAuditRequest request, CancellationToken cancellationToken)
    {
        var entry = AuditEntry.From(request, time.GetUtcNow());
        if (await repository.TryInsertAsync(entry, cancellationToken))
        {
            logger.LogInformation("Recorded audit entry for job {JobId} ({Status})", request.JobId, request.Status);
            return new RecordResult(true, entry);
        }

        var existing = await repository.GetAsync(request.JobId, cancellationToken);
        return new RecordResult(false, existing ?? entry);
    }

    public Task<AuditEntry?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        repository.GetAsync(jobId, cancellationToken);
}

public static class JobAuditRequestValidator
{
    public static IReadOnlyDictionary<string, string[]> Validate(JobAuditRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }

        if (request.JobId == Guid.Empty)
        {
            errors["jobId"] = ["JobId must not be empty."];
        }

        if (!AuditJobStatuses.IsTerminal(request.Status))
        {
            errors["status"] = [$"Status must be one of {AuditJobStatuses.Completed}, {AuditJobStatuses.Failed}, {AuditJobStatuses.Cancelled}."];
        }

        if (request.TotalDocuments < 0 || request.SucceededDocuments < 0 || request.FailedDocuments < 0 || request.TotalWords < 0)
        {
            errors["counts"] = ["Counts must not be negative."];
        }
        else if (request.SucceededDocuments + request.FailedDocuments > request.TotalDocuments)
        {
            errors["counts"] = ["Succeeded + failed documents cannot exceed the total."];
        }

        if (request.Name is { Length: > JobLimits.MaxJobNameLength })
        {
            errors["name"] = [$"Name must be at most {JobLimits.MaxJobNameLength} characters."];
        }

        if (request.FailureReason is not null && Encoding.UTF8.GetByteCount(request.FailureReason) > 4096)
        {
            errors["failureReason"] = ["Failure reason must be at most 4 KB."];
        }

        if (request.CompletedAt < request.CreatedAt)
        {
            errors["completedAt"] = ["CompletedAt cannot be earlier than CreatedAt."];
        }

        return errors;
    }
}
