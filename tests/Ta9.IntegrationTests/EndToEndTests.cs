using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Ta9.Contracts.Audit;
using Ta9.Contracts.Processing;
using Ta9.IntegrationTests.Infrastructure;
using Ta9.JobService.Api;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Xunit.Abstractions;

namespace Ta9.IntegrationTests;

[Collection(EndToEndCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EndToEndTests(MongoContainerFixture mongo, ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly JobStatus[] Terminal = [JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled];

    /// <summary>31 words, 4 unique: "alpha beta gamma" × 10 + "doc{i}".</summary>
    private static string Document(int i) => string.Join(' ', Enumerable.Repeat("alpha beta gamma", 10)) + $" doc{i}";

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(100);
        }
    }

    private static async Task<JobAuditResponse?> TryGetAuditAsync(Ta9System system, Guid jobId)
    {
        var response = await system.AuditApi.GetAsync($"/api/audit/{jobId}");
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JobAuditResponse>() : null;
    }

    [Fact]
    public async Task Job_flows_through_processor_websocket_and_audit()
    {
        await using var system = Ta9System.Start(mongo.ConnectionString);

        var created = await system.CreateJobAsync(["hello world hello", "The cat and the hat", ""]);
        created.Status.Should().Be(JobStatus.Pending);

        var completed = await system.WaitForStatusAsync(created.Id, Timeout, Terminal);

        completed.Status.Should().Be(JobStatus.Completed, completed.FailureReason);
        completed.Summary!.Succeeded.Should().Be(3);
        var results = (await system.JobsApi.GetFromJsonAsync<PagedResponse<JobDocumentResult>>(
            $"/api/jobs/{created.Id}/results", Ta9System.Json))!;
        results.Items.Select(r => r.Result!.WordCount).Should().Equal(3, 5, 0);
        results.Items[0].Result!.TopWords[0].Should().Be(new WordCountDto("hello", 2));
        results.Items[0].Result!.Sha256.Should().HaveLength(64);

        var audit = await EventuallyAsync(() => TryGetAuditAsync(system, created.Id), Timeout);
        audit.Status.Should().Be(AuditJobStatuses.Completed);
        audit.SucceededDocuments.Should().Be(3);
    }

    [Fact]
    public async Task Job_flow_is_logged_to_mongodb_by_all_three_services_under_one_correlation_id()
    {
        await using var system = Ta9System.Start(mongo.ConnectionString);
        var created = await system.CreateJobAsync(["alpha beta", "gamma"]);
        await system.WaitForStatusAsync(created.Id, Timeout, Terminal);
        await EventuallyAsync(() => TryGetAuditAsync(system, created.Id), Timeout);

        var logs = await EventuallyAsync(async () =>
        {
            var records = await system.GetJobLogsAsync(created.Id);
            return records.Any(r => r["service"] == "audit-service") && records.Any(r => r["message"].AsString.StartsWith("Applied JobCompleted"))
                ? records
                : null;
        }, Timeout);

        string Message(BsonDocument r) => $"{r["service"]}: {r["message"]}";
        logs.Select(Message).Should().Contain(
        [
            $"job-service: Job {created.Id} created with 2 documents",
            "processor-service: Job started with 2 documents",
            $"job-service: Applied JobCompleted to job {created.Id}"
        ]);
        logs.Should().Contain(r => r["service"] == "processor-service" && r["message"].AsString.StartsWith("Job completed: 2 succeeded"));
        logs.Should().Contain(r => r["service"] == "audit-service" && r["message"].AsString.StartsWith($"Recorded audit entry for job {created.Id}"));
        logs.Should().Contain(r => r["level"] == "Debug", "Debug details are stored in MongoDB");
        logs.Where(r => r.Contains("correlationId")).Select(r => r["correlationId"].AsString).Distinct()
            .Should().ContainSingle("one correlation id follows the job through all three services");
        logs.Where(r => r["level"] != "Debug").Should().OnlyContain(r => r.Contains("correlationId"));
        output.WriteLine(string.Join(Environment.NewLine, logs.Select(r => $"{r["timestamp"]} {r["level"],-11} {Message(r)}")));
    }

    [Fact]
    public async Task Ten_concurrent_jobs_of_1000_documents_complete_with_bounded_parallelism()
    {
        const int jobCount = 10;
        const int documentsPerJob = 1000;
        var analyzer = new ConcurrencyTrackingAnalyzer();
        await using var system = Ta9System.Start(mongo.ConnectionString, new SystemOptions { Analyzer = analyzer });
        var documents = Enumerable.Range(0, documentsPerJob).Select(Document).ToList();
        var stopwatch = Stopwatch.StartNew();

        var jobs = await Task.WhenAll(Enumerable.Range(0, jobCount).Select(i => system.CreateJobAsync(documents, $"load-{i}")));
        var finished = await Task.WhenAll(jobs.Select(job => system.WaitForStatusAsync(job.Id, TimeSpan.FromMinutes(3), Terminal)));
        stopwatch.Stop();

        output.WriteLine($"{jobCount} jobs x {documentsPerJob} documents completed in {stopwatch.Elapsed.TotalSeconds:F1}s; " +
                         $"max concurrent analyses: {analyzer.MaxConcurrency}");

        finished.Should().OnlyContain(job => job.Status == JobStatus.Completed);
        finished.Should().OnlyContain(job => job.Summary!.Succeeded == documentsPerJob && job.Summary.TotalWords == 31L * documentsPerJob);
        analyzer.Calls.Should().Be(jobCount * documentsPerJob, "every document is analyzed exactly once");
        analyzer.MaxConcurrency.Should().BeLessThanOrEqualTo(10 * 2, "MaxConcurrentJobs (10) x DocParallelismPerJob (2)");

        var lastPage = (await system.JobsApi.GetFromJsonAsync<PagedResponse<JobDocumentResult>>(
            $"/api/jobs/{jobs[0].Id}/results?page=5&pageSize=200", Ta9System.Json))!;
        lastPage.TotalCount.Should().Be(documentsPerJob);
        lastPage.Items.Should().HaveCount(200).And.OnlyContain(r => r.Result!.WordCount == 31 && r.Result.UniqueWordCount == 4);
    }

    [Fact]
    public async Task Unreachable_processor_fails_the_job_after_retries()
    {
        await using var system = Ta9System.Start(mongo.ConnectionString, new SystemOptions { ProcessorReachable = false, MaxDispatchAttempts = 3 });

        var created = await system.CreateJobAsync(["some text"]);
        var failed = await system.WaitForStatusAsync(created.Id, Timeout, Terminal);

        failed.Status.Should().Be(JobStatus.Failed);
        failed.FailureReason.Should().Contain("Processor unreachable after 3 attempts");
        var audit = await EventuallyAsync(() => TryGetAuditAsync(system, created.Id), Timeout);
        audit.Status.Should().Be(AuditJobStatuses.Failed);
    }

    [Fact]
    public async Task Job_lost_by_a_processor_restart_is_redispatched_and_completes()
    {
        using var gate = new ManualResetEventSlim(false);
        await using var system = Ta9System.Start(mongo.ConnectionString, new SystemOptions
        {
            Analyzer = new GatedAnalyzer(gate),
            ProcessorShutdownTimeout = TimeSpan.FromSeconds(2)
        });

        try
        {
            var created = await system.CreateJobAsync(Enumerable.Range(0, 10).Select(Document).ToList());
            await system.WaitForStatusAsync(created.Id, Timeout, JobStatus.Processing);

            // The processor holding the job dies (its memory is gone); a fresh one takes its place.
            await system.RestartProcessorAsync();

            var completed = await system.WaitForStatusAsync(created.Id, Timeout, Terminal);
            completed.Status.Should().Be(JobStatus.Completed, completed.FailureReason);
            completed.Summary!.Succeeded.Should().Be(10);

            var stored = await system.Jobs.Services.GetRequiredService<IJobRepository>().GetAsync(created.Id, CancellationToken.None);
            stored!.RedispatchCount.Should().Be(1, "the reconciler found the job missing on the new processor");
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Cancelling_a_running_job_stops_it_on_the_processor()
    {
        using var gate = new ManualResetEventSlim(false);
        await using var system = Ta9System.Start(mongo.ConnectionString, new SystemOptions { Analyzer = new GatedAnalyzer(gate) });

        try
        {
            var created = await system.CreateJobAsync(Enumerable.Range(0, 10).Select(Document).ToList());
            await system.WaitForStatusAsync(created.Id, Timeout, JobStatus.Processing);

            var cancel = await system.JobsApi.PostAsync($"/api/jobs/{created.Id}/cancel", null);
            cancel.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var onProcessor = await EventuallyAsync(async () =>
            {
                var status = await system.ProcessorApi.GetFromJsonAsync<ProcessJobStatusResponse>($"/api/process/{created.Id}");
                return status!.Status == ProcessingStatus.Cancelled ? status : null;
            }, Timeout);
            onProcessor.Status.Should().Be(ProcessingStatus.Cancelled);

            gate.Set(); // let in-flight analyses finish: a late completion must not resurrect the job
            await Task.Delay(TimeSpan.FromSeconds(1));
            (await system.GetJobAsync(created.Id)).Status.Should().Be(JobStatus.Cancelled);

            var audit = await EventuallyAsync(() => TryGetAuditAsync(system, created.Id), Timeout);
            audit.Status.Should().Be(AuditJobStatuses.Cancelled);
        }
        finally
        {
            gate.Set();
        }
    }
}
