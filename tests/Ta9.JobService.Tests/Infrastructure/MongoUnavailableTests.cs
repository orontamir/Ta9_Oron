using MongoDB.Driver;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.Mongo;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Infrastructure;

/// <summary>
/// An unreachable MongoDB surfaces as the storage-neutral <see cref="StorageUnavailableException"/>, so nothing above
/// the repositories depends on driver types. No Docker needed: the client points at a closed port.
/// </summary>
public class MongoUnavailableTests
{
    private static IMongoDatabase UnreachableDatabase()
    {
        var settings = new MongoClientSettings
        {
            Server = new MongoServerAddress("127.0.0.1", 1),
            ServerSelectionTimeout = TimeSpan.FromMilliseconds(300),
            ConnectTimeout = TimeSpan.FromMilliseconds(300)
        };
        return new MongoClient(settings).GetDatabase("unreachable");
    }

    [Fact]
    public async Task Job_repository_reads_and_writes_report_storage_unavailable()
    {
        var jobs = new MongoJobRepository(UnreachableDatabase());

        await FluentActions.Awaiting(() => jobs.GetAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<StorageUnavailableException>();
        await FluentActions.Awaiting(() => jobs.InsertAsync(TestJobs.New(), CancellationToken.None))
            .Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task Failed_update_keeps_the_version_token_unchanged()
    {
        var jobs = new MongoJobRepository(UnreachableDatabase());
        var job = TestJobs.InStatus(JobStatus.Queued);
        var versionBefore = job.Version;

        await FluentActions.Awaiting(() => jobs.TryUpdateAsync(job, CancellationToken.None))
            .Should().ThrowAsync<StorageUnavailableException>();

        job.Version.Should().Be(versionBefore);
    }

    [Fact]
    public async Task Document_repository_reports_storage_unavailable()
    {
        var documents = new MongoJobDocumentRepository(UnreachableDatabase());

        await FluentActions.Awaiting(() => documents.GetResultsAsync(Guid.NewGuid(), 1, 10, CancellationToken.None))
            .Should().ThrowAsync<StorageUnavailableException>();
    }
}
