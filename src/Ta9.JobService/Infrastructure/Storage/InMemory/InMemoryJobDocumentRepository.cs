using System.Collections.Concurrent;
using Ta9.Contracts.Processing;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Infrastructure.Storage.InMemory;

public sealed class InMemoryJobDocumentRepository : IJobDocumentRepository
{
    private readonly ConcurrentDictionary<string, JobDocument> _documents = new();

    public Task InsertManyAsync(IReadOnlyList<JobDocument> documents, CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            _documents[document.Id] = document.Clone();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobDocument>> GetByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JobDocument>>(OfJob(jobId).Select(d => d.Clone()).ToList());

    public Task SaveResultsAsync(Guid jobId, IReadOnlyList<DocumentResultDto> results, CancellationToken cancellationToken)
    {
        foreach (var result in results)
        {
            // Results for unknown documents are ignored, like the Mongo update without upsert.
            var id = JobDocument.MakeId(jobId, result.Index);
            if (_documents.TryGetValue(id, out var existing))
            {
                var updated = existing.Clone();
                updated.Result = result;
                _documents[id] = updated;
            }
        }

        return Task.CompletedTask;
    }

    public Task<PagedResult<JobDocumentResult>> GetResultsAsync(Guid jobId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var documents = OfJob(jobId).ToList();
        var items = documents
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new JobDocumentResult(d.Index, d.Name, d.Result))
            .ToList();

        return Task.FromResult(new PagedResult<JobDocumentResult>(items, page, pageSize, documents.Count));
    }

    public Task DeleteByJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        foreach (var document in OfJob(jobId))
        {
            _documents.TryRemove(document.Id, out _);
        }

        return Task.CompletedTask;
    }

    private IEnumerable<JobDocument> OfJob(Guid jobId) =>
        _documents.Values.Where(d => d.JobId == jobId).OrderBy(d => d.Index);
}
