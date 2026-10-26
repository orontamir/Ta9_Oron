using MongoDB.Driver;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Infrastructure.Storage.Mongo;

public sealed class MongoJobRepository : IJobRepository
{
    public const string CollectionName = "jobs";

    private readonly IMongoCollection<Job> _jobs;

    public MongoJobRepository(IMongoDatabase database)
    {
        MongoMappings.Register();
        _jobs = database.GetCollection<Job>(CollectionName);
    }

    public Task InsertAsync(Job job, CancellationToken cancellationToken) =>
        _jobs.InsertOneAsync(job, options: null, cancellationToken);

    public async Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await _jobs.Find(job => job.Id == id).FirstOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var filter = status is null
            ? Builders<Job>.Filter.Empty
            : Builders<Job>.Filter.Eq(job => job.Status, status.Value);

        var total = await _jobs.CountDocumentsAsync(filter, options: null, cancellationToken);
        var items = await _jobs.Find(filter)
            .SortByDescending(job => job.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Job>(items, page, pageSize, total);
    }

    public async Task<bool> TryUpdateAsync(Job job, CancellationToken cancellationToken)
    {
        var expectedVersion = job.Version;
        job.Version = expectedVersion + 1;

        var result = await _jobs.ReplaceOneAsync(
            stored => stored.Id == job.Id && stored.Version == expectedVersion,
            job,
            new ReplaceOptions { IsUpsert = false },
            cancellationToken);

        if (result.MatchedCount == 1)
        {
            return true;
        }

        job.Version = expectedVersion;
        return false;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        (await _jobs.DeleteOneAsync(job => job.Id == id, cancellationToken)).DeletedCount == 1;

    public Task EnsureIndexesAsync(CancellationToken cancellationToken) =>
        _jobs.Indexes.CreateManyAsync(
        [
            // Dispatcher: Pending jobs due for dispatch.
            new CreateIndexModel<Job>(Builders<Job>.IndexKeys.Ascending(job => job.Status).Ascending(job => job.NextDispatchAt)),
            // Reconciler: active jobs without recent updates.
            new CreateIndexModel<Job>(Builders<Job>.IndexKeys.Ascending(job => job.Status).Ascending(job => job.UpdatedAt)),
            // Listing newest first.
            new CreateIndexModel<Job>(Builders<Job>.IndexKeys.Descending(job => job.CreatedAt))
        ],
        cancellationToken);
}
