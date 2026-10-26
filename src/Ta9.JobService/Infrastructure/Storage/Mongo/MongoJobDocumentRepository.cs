using MongoDB.Driver;
using Ta9.Contracts.Processing;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Infrastructure.Storage.Mongo;

public sealed class MongoJobDocumentRepository : IJobDocumentRepository
{
    public const string CollectionName = "job_documents";

    private readonly IMongoCollection<JobDocument> _documents;

    public MongoJobDocumentRepository(IMongoDatabase database)
    {
        MongoMappings.Register();
        _documents = database.GetCollection<JobDocument>(CollectionName);
    }

    public Task InsertManyAsync(IReadOnlyList<JobDocument> documents, CancellationToken cancellationToken) =>
        _documents.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = false }, cancellationToken);

    public async Task<IReadOnlyList<JobDocument>> GetByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        await _documents.Find(document => document.JobId == jobId)
            .SortBy(document => document.Index)
            .ToListAsync(cancellationToken);

    public async Task SaveResultsAsync(Guid jobId, IReadOnlyList<DocumentResultDto> results, CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        var writes = results
            .Select(result => new UpdateOneModel<JobDocument>(
                Builders<JobDocument>.Filter.Eq(document => document.Id, JobDocument.MakeId(jobId, result.Index)),
                Builders<JobDocument>.Update.Set(document => document.Result, result)))
            .ToList();

        await _documents.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
    }

    public async Task<PagedResult<JobDocumentResult>> GetResultsAsync(Guid jobId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var filter = Builders<JobDocument>.Filter.Eq(document => document.JobId, jobId);
        var total = await _documents.CountDocumentsAsync(filter, options: null, cancellationToken);
        var documents = await _documents.Find(filter)
            .SortBy(document => document.Index)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .Project<JobDocument>(Builders<JobDocument>.Projection.Exclude(document => document.Content))
            .ToListAsync(cancellationToken);

        var items = documents.Select(d => new JobDocumentResult(d.Index, d.Name, d.Result)).ToList();
        return new PagedResult<JobDocumentResult>(items, page, pageSize, total);
    }

    public Task DeleteByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        _documents.DeleteManyAsync(document => document.JobId == jobId, cancellationToken);

    public Task EnsureIndexesAsync(CancellationToken cancellationToken) =>
        _documents.Indexes.CreateOneAsync(
            new CreateIndexModel<JobDocument>(
                Builders<JobDocument>.IndexKeys.Ascending(document => document.JobId).Ascending(document => document.Index),
                new CreateIndexOptions { Unique = true }),
            options: null,
            cancellationToken);
}
