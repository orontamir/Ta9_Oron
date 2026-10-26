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
        MongoGuard.RunAsync(() => _jobs.InsertOneAsync(job, options: null, cancellationToken));

    public Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        MongoGuard.RunAsync<Job?>(async () => await _jobs.Find(job => job.Id == id).FirstOrDefaultAsync(cancellationToken));

    public Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken cancellationToken) =>
        MongoGuard.RunAsync(async () =>
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
        });

    public Task<bool> TryUpdateAsync(Job job, CancellationToken cancellationToken) =>
        MongoGuard.RunAsync(async () =>
        {
            var expectedVersion = job.Version;
            job.Version = expectedVersion + 1;
            try
            {
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
            catch
            {
                job.Version = expectedVersion; // nothing was written: keep the caller's token consistent
                throw;
            }
        });

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        MongoGuard.RunAsync(async () => (await _jobs.DeleteOneAsync(job => job.Id == id, cancellationToken)).DeletedCount == 1);

    public Task<Job?> TryClaimNextForDispatchAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken) =>
        // A single findOneAndUpdate is atomic, so concurrent dispatch loops (or instances) never claim the same job.
        MongoGuard.RunAsync<Job?>(async () => await _jobs.FindOneAndUpdateAsync(
            Builders<Job>.Filter.And(
                Builders<Job>.Filter.Eq(job => job.Status, JobStatus.Pending),
                Builders<Job>.Filter.Lte(job => job.NextDispatchAt, now),
                Builders<Job>.Filter.Or(
                    Builders<Job>.Filter.Eq(job => job.LeaseUntil, null),
                    Builders<Job>.Filter.Lte(job => job.LeaseUntil, now))),
            Builders<Job>.Update.Set(job => job.LeaseUntil, leaseUntil).Inc(job => job.Version, 1L),
            new FindOneAndUpdateOptions<Job>
            {
                Sort = Builders<Job>.Sort.Ascending(job => job.NextDispatchAt),
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken));

    public Task<IReadOnlyList<Job>> FindStaleActiveAsync(DateTimeOffset updatedBefore, int limit, CancellationToken cancellationToken) =>
        MongoGuard.RunAsync<IReadOnlyList<Job>>(async () => await _jobs.Find(Builders<Job>.Filter.And(
                Builders<Job>.Filter.In(job => job.Status, new[] { JobStatus.Queued, JobStatus.Processing }),
                Builders<Job>.Filter.Lt(job => job.UpdatedAt, updatedBefore)))
            .SortBy(job => job.UpdatedAt)
            .Limit(limit)
            .ToListAsync(cancellationToken));

    public Task<IReadOnlyList<Job>> FindUnpublishedTerminalAsync(int limit, CancellationToken cancellationToken) =>
        MongoGuard.RunAsync<IReadOnlyList<Job>>(async () => await _jobs.Find(Builders<Job>.Filter.And(
                Builders<Job>.Filter.Eq(job => job.AuditPublished, false),
                Builders<Job>.Filter.In(job => job.Status, new[] { JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled })))
            .SortBy(job => job.UpdatedAt)
            .Limit(limit)
            .ToListAsync(cancellationToken));

    public Task EnsureIndexesAsync(CancellationToken cancellationToken) =>
        _jobs.Indexes.CreateManyAsync(
        [
            // Dispatcher: Pending jobs due for dispatch.
            new CreateIndexModel<Job>(Builders<Job>.IndexKeys.Ascending(job => job.Status).Ascending(job => job.NextDispatchAt)),
            // Reconciler: active jobs without recent updates.
            new CreateIndexModel<Job>(Builders<Job>.IndexKeys.Ascending(job => job.Status).Ascending(job => job.UpdatedAt)),
            // Audit publisher: terminal jobs not yet published.
            new CreateIndexModel<Job>(Builders<Job>.IndexKeys.Ascending(job => job.AuditPublished).Ascending(job => job.Status)),
            // Listing newest first.
            new CreateIndexModel<Job>(Builders<Job>.IndexKeys.Descending(job => job.CreatedAt))
        ],
        cancellationToken);
}
