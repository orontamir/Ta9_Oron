using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Infrastructure.Storage.Mongo;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Infrastructure;

public abstract class JobDocumentRepositoryContract
{
    protected abstract IJobDocumentRepository Documents { get; }

    private static readonly CancellationToken None = CancellationToken.None;

    private async Task<Guid> InsertJobDocumentsAsync(int count)
    {
        var jobId = Guid.NewGuid();
        // Inserted in reverse to prove reads are ordered by index, not insertion.
        var documents = Enumerable.Range(0, count).Reverse()
            .Select(i => JobDocument.Create(jobId, i, $"doc-{i}.txt", $"content {i}"))
            .ToList();
        await Documents.InsertManyAsync(documents, None);
        return jobId;
    }

    [Fact]
    public async Task Returns_documents_of_one_job_ordered_by_index()
    {
        var jobId = await InsertJobDocumentsAsync(3);
        await InsertJobDocumentsAsync(2); // another job

        var documents = await Documents.GetByJobAsync(jobId, None);

        documents.Select(d => d.Index).Should().Equal(0, 1, 2);
        documents.Should().OnlyContain(d => d.JobId == jobId);
        documents[1].Content.Should().Be("content 1");
    }

    [Fact]
    public async Task Saving_results_is_idempotent()
    {
        var jobId = await InsertJobDocumentsAsync(2);
        var results = new[] { TestJobs.Result(0, "doc-0.txt"), TestJobs.Result(1, "doc-1.txt") };

        await Documents.SaveResultsAsync(jobId, results, None);
        await Documents.SaveResultsAsync(jobId, results, None);

        var page = await Documents.GetResultsAsync(jobId, page: 1, pageSize: 10, None);
        page.TotalCount.Should().Be(2);
        page.Items.Select(r => r.Result).Should().BeEquivalentTo(results, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Results_are_paged_by_index()
    {
        var jobId = await InsertJobDocumentsAsync(5);

        var page = await Documents.GetResultsAsync(jobId, page: 2, pageSize: 2, None);

        page.TotalCount.Should().Be(5);
        page.Items.Select(r => r.Index).Should().Equal(2, 3);
        page.Items.Should().OnlyContain(r => r.Result == null, "nothing was processed yet");
    }

    [Fact]
    public async Task Results_for_unknown_documents_are_ignored()
    {
        var jobId = await InsertJobDocumentsAsync(1);

        await Documents.SaveResultsAsync(jobId, [TestJobs.Result(0), TestJobs.Result(7)], None);

        (await Documents.GetResultsAsync(jobId, 1, 10, None)).TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Delete_removes_only_the_given_job()
    {
        var jobId = await InsertJobDocumentsAsync(2);
        var otherJobId = await InsertJobDocumentsAsync(2);

        await Documents.DeleteByJobAsync(jobId, None);

        (await Documents.GetByJobAsync(jobId, None)).Should().BeEmpty();
        (await Documents.GetByJobAsync(otherJobId, None)).Should().HaveCount(2);
    }
}

public sealed class InMemoryJobDocumentRepositoryTests : JobDocumentRepositoryContract
{
    protected override IJobDocumentRepository Documents { get; } = new InMemoryJobDocumentRepository();
}

[Collection(MongoCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MongoJobDocumentRepositoryTests(MongoFixture mongo) : JobDocumentRepositoryContract
{
    protected override IJobDocumentRepository Documents { get; } = new MongoJobDocumentRepository(mongo.CreateDatabase());
}
