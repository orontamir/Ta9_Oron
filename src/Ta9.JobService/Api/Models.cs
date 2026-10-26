using Ta9.Contracts.Processing;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Api;

public sealed record CreateJobDocumentRequest(string Name, string Content);

public sealed record CreateJobRequest(string? Name, IReadOnlyList<CreateJobDocumentRequest> Documents)
{
    public CreateJobCommand ToCommand() =>
        new(Name, Documents.Select(d => new NewDocument(d.Name, d.Content)).ToList());
}

public sealed record JobResponse(
    Guid Id,
    string? Name,
    JobStatus Status,
    int TotalDocuments,
    int ProcessedDocuments,
    double ProgressPercent,
    ProcessingSummaryDto? Summary,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public static JobResponse From(Job job) => new(
        job.Id,
        job.Name,
        job.Status,
        job.TotalDocuments,
        job.ProcessedDocuments,
        job.TotalDocuments == 0 ? 0 : Math.Round(100.0 * job.ProcessedDocuments / job.TotalDocuments, 1),
        job.Summary,
        job.FailureReason,
        job.CreatedAt,
        job.UpdatedAt,
        job.StartedAt,
        job.CompletedAt);
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public static PagedResponse<T> From<TSource>(PagedResult<TSource> page, Func<TSource, T> map) =>
        new(page.Items.Select(map).ToList(), page.Page, page.PageSize, page.TotalCount);
}
