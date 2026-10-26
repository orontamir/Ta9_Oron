using Microsoft.Extensions.Logging.Abstractions;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Tests.TestSupport;
using Ta9.ServiceDefaults;

namespace Ta9.JobService.Tests.Application;

public class JobManagerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly InMemoryJobRepository _jobs = new();
    private readonly InMemoryJobDocumentRepository _documents = new();
    private readonly JobManager _manager;

    public JobManagerTests() =>
        _manager = new JobManager(_jobs, _documents, TimeProvider.System, NullLogger<JobManager>.Instance);

    private static CreateJobCommand Command(params string[] contents) =>
        new("job", contents.Select((c, i) => new NewDocument($"doc-{i}", c)).ToList());

    [Fact]
    public async Task Create_stores_job_and_documents_in_order()
    {
        Job job;
        using (Correlation.BeginScope("corr-42"))
        {
            job = await _manager.CreateAsync(Command("first", "second"), None);
        }

        var stored = await _jobs.GetAsync(job.Id, None);
        stored!.Status.Should().Be(JobStatus.Pending);
        stored.TotalDocuments.Should().Be(2);
        stored.CorrelationId.Should().Be("corr-42");
        (await _documents.GetByJobAsync(job.Id, None)).Select(d => d.Content).Should().Equal("first", "second");
    }

    [Fact]
    public async Task Results_are_not_available_before_completion()
    {
        var job = await _manager.CreateAsync(Command("a"), None);

        (await _manager.GetResultsAsync(job.Id, 1, 10, None)).Outcome.Should().Be(ResultsOutcome.NotCompleted);
        (await _manager.GetResultsAsync(Guid.NewGuid(), 1, 10, None)).Outcome.Should().Be(ResultsOutcome.NotFound);
    }

    [Theory]
    [InlineData(JobStatus.Pending, CancelJobOutcome.Cancelled)]
    [InlineData(JobStatus.Queued, CancelJobOutcome.Cancelled)]
    [InlineData(JobStatus.Processing, CancelJobOutcome.Cancelled)]
    [InlineData(JobStatus.Cancelled, CancelJobOutcome.Cancelled)]
    [InlineData(JobStatus.Completed, CancelJobOutcome.AlreadyFinished)]
    [InlineData(JobStatus.Failed, CancelJobOutcome.AlreadyFinished)]
    public async Task Cancel_outcome_depends_on_status(JobStatus status, CancelJobOutcome expected)
    {
        var job = TestJobs.InStatus(status);
        await _jobs.InsertAsync(job, None);

        var result = await _manager.CancelAsync(job.Id, None);

        result.Outcome.Should().Be(expected);
    }

    [Theory]
    [InlineData(JobStatus.Pending, DeleteJobOutcome.StillActive)]
    [InlineData(JobStatus.Processing, DeleteJobOutcome.StillActive)]
    [InlineData(JobStatus.Completed, DeleteJobOutcome.Deleted)]
    [InlineData(JobStatus.Failed, DeleteJobOutcome.Deleted)]
    [InlineData(JobStatus.Cancelled, DeleteJobOutcome.Deleted)]
    public async Task Delete_outcome_depends_on_status(JobStatus status, DeleteJobOutcome expected)
    {
        var job = TestJobs.InStatus(status);
        await _jobs.InsertAsync(job, None);
        await _documents.InsertManyAsync([JobDocument.Create(job.Id, 0, "d", "c")], None);

        var outcome = await _manager.DeleteAsync(job.Id, None);

        outcome.Should().Be(expected);
        var documentsLeft = (await _documents.GetByJobAsync(job.Id, None)).Count;
        documentsLeft.Should().Be(expected == DeleteJobOutcome.Deleted ? 0 : 1);
    }
}
