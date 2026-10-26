using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ta9.Contracts;
using Ta9.JobService.Api;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Tests.TestSupport;
using Ta9.ServiceDefaults;

namespace Ta9.JobService.Tests.Api;

public sealed class JobsApiTests : IClassFixture<JobServiceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly JobServiceFactory _factory;
    private readonly HttpClient _client;

    public JobsApiTests(JobServiceFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CreateJobRequest Request(params string[] contents) =>
        new("api-test", contents.Select((c, i) => new CreateJobDocumentRequest($"doc-{i}.txt", c)).ToList());

    private async Task<JobResponse> CreateJobAsync(params string[] contents)
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", Request(contents.Length == 0 ? new[] { "hello world" } : contents));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobResponse>(Json))!;
    }

    private async Task<ValidationProblemDetails> ExpectValidationProblemAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }

    [Fact]
    public async Task Create_returns_201_with_location_and_pending_job()
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", Request("hello world", "second doc"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = (await response.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        job.Status.Should().Be(JobStatus.Pending);
        job.TotalDocuments.Should().Be(2);
        job.Name.Should().Be("api-test");
        response.Headers.Location!.ToString().Should().EndWith($"/api/jobs/{job.Id}");

        var stored = await _factory.Documents.GetByJobAsync(job.Id, CancellationToken.None);
        stored.Select(d => d.Content).Should().Equal("hello world", "second doc");
    }

    [Fact]
    public async Task Create_accepts_empty_content() =>
        (await CreateJobAsync("")).TotalDocuments.Should().Be(1);

    [Fact]
    public async Task Create_records_the_callers_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs") { Content = JsonContent.Create(Request("a")) };
        request.Headers.Add(Correlation.HeaderName, "client-corr-7");

        var response = await _client.SendAsync(request);
        var job = (await response.Content.ReadFromJsonAsync<JobResponse>(Json))!;

        response.Headers.GetValues(Correlation.HeaderName).Should().ContainSingle().Which.Should().Be("client-corr-7");
        (await _factory.Jobs.GetAsync(job.Id, CancellationToken.None))!.CorrelationId.Should().Be("client-corr-7");
    }

    [Fact]
    public async Task Create_without_documents_is_rejected() =>
        (await ExpectValidationProblemAsync(new { name = "x", documents = Array.Empty<object>() }))
            .Errors.Should().ContainKey("documents");

    [Fact]
    public async Task Create_with_too_many_documents_is_rejected()
    {
        var documents = Enumerable.Range(0, JobLimits.MaxDocumentsPerJob + 1).Select(i => new { name = $"d{i}", content = "x" });

        (await ExpectValidationProblemAsync(new { documents })).Errors.Should().ContainKey("documents");
    }

    [Fact]
    public async Task Create_with_null_content_or_missing_name_is_rejected()
    {
        var problem = await ExpectValidationProblemAsync(new
        {
            documents = new object[]
            {
                new { name = "ok", content = (string?)null },
                new { name = "", content = "text" }
            }
        });

        problem.Errors.Should().ContainKey("documents[0].content").And.ContainKey("documents[1].name");
    }

    [Fact]
    public async Task Create_with_oversized_document_is_rejected()
    {
        var big = new string('x', JobLimits.MaxDocumentBytes + 1);

        (await ExpectValidationProblemAsync(new { documents = new[] { new { name = "big", content = big } } }))
            .Errors.Should().ContainKey("documents[0].content");
    }

    [Fact]
    public async Task Create_with_malformed_json_returns_400()
    {
        var response = await _client.PostAsync("/api/jobs", new StringContent("{ nope", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_returns_the_job_or_404()
    {
        var created = await CreateJobAsync();

        (await _client.GetFromJsonAsync<JobResponse>($"/api/jobs/{created.Id}", Json))!.Id.Should().Be(created.Id);

        var missing = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task List_pages_newest_first_and_filters_by_status()
    {
        var first = await CreateJobAsync();
        var second = await CreateJobAsync();
        var third = await CreateJobAsync();
        await _factory.SetStatusAsync(second.Id, j => j.TryCancel(DateTimeOffset.UtcNow));

        var page = (await _client.GetFromJsonAsync<PagedResponse<JobResponse>>("/api/jobs?pageSize=2", Json))!;
        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().BeGreaterThanOrEqualTo(3);
        page.Items.Select(j => j.CreatedAt).Should().BeInDescendingOrder();

        var cancelled = (await _client.GetFromJsonAsync<PagedResponse<JobResponse>>("/api/jobs?status=Cancelled&pageSize=100", Json))!;
        cancelled.Items.Should().Contain(j => j.Id == second.Id).And.NotContain(j => j.Id == first.Id || j.Id == third.Id);
    }

    [Theory]
    [InlineData("/api/jobs?page=0")]
    [InlineData("/api/jobs?pageSize=101")]
    [InlineData("/api/jobs?status=Bogus")]
    public async Task List_rejects_invalid_query(string url) =>
        (await _client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Results_are_409_until_the_job_completes_then_paged()
    {
        var job = await CreateJobAsync("a", "b", "c");

        (await _client.GetAsync($"/api/jobs/{job.Id}/results")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await _factory.CompleteAsync(job.Id, 3);
        var page = (await _client.GetFromJsonAsync<PagedResponse<JobDocumentResult>>($"/api/jobs/{job.Id}/results?page=2&pageSize=2", Json))!;

        page.TotalCount.Should().Be(3);
        page.Items.Should().ContainSingle().Which.Index.Should().Be(2);
        page.Items[0].Result!.WordCount.Should().Be(3);
    }

    [Fact]
    public async Task Results_of_unknown_job_is_404() =>
        (await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}/results")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Cancel_is_idempotent_and_conflicts_once_finished()
    {
        var job = await CreateJobAsync();

        var first = await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await first.Content.ReadFromJsonAsync<JobResponse>(Json))!.Status.Should().Be(JobStatus.Cancelled);
        (await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var completed = await CreateJobAsync();
        await _factory.CompleteAsync(completed.Id, 1);
        (await _client.PostAsync($"/api/jobs/{completed.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _client.PostAsync($"/api/jobs/{Guid.NewGuid()}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancelling_a_dispatched_job_tells_the_processor()
    {
        var job = await CreateJobAsync();
        await _factory.SetStatusAsync(job.Id, j => j.TryMarkQueued(DateTimeOffset.UtcNow));

        (await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        _factory.Processor.Cancelled.Should().Contain(job.Id);
    }

    [Fact]
    public async Task Delete_requires_a_finished_job_and_removes_its_documents()
    {
        var job = await CreateJobAsync("a", "b");

        (await _client.DeleteAsync($"/api/jobs/{job.Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null);
        (await _client.DeleteAsync($"/api/jobs/{job.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.GetAsync($"/api/jobs/{job.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.Documents.GetByJobAsync(job.Id, CancellationToken.None)).Should().BeEmpty();
        (await _client.DeleteAsync($"/api/jobs/{job.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Readiness_reports_storage_health() =>
        (await _client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
}

/// <summary>Storage outage must surface as a retryable 503, not a 500.</summary>
public sealed class JobsApiStorageFailureTests : IClassFixture<JobServiceFactory>
{
    private sealed class UnreachableJobRepository : IJobRepository
    {
        private static Exception Down() =>
            new StorageUnavailableException("MongoDB is unreachable.", new TimeoutException("A timeout occurred after 5000ms selecting a server."));

        public Task InsertAsync(Job job, CancellationToken ct) => throw Down();
        public Task<Job?> GetAsync(Guid id, CancellationToken ct) => throw Down();
        public Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken ct) => throw Down();
        public Task<bool> TryUpdateAsync(Job job, CancellationToken ct) => throw Down();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw Down();
        public Task<Job?> TryClaimNextForDispatchAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct) => throw Down();
        public Task<IReadOnlyList<Job>> FindStaleActiveAsync(DateTimeOffset updatedBefore, int limit, CancellationToken ct) => throw Down();
        public Task<IReadOnlyList<Job>> FindUnpublishedTerminalAsync(int limit, CancellationToken ct) => throw Down();
    }

    private readonly HttpClient _client;

    public JobsApiStorageFailureTests(JobServiceFactory factory) =>
        _client = factory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.AddSingleton<IJobRepository>(new UnreachableJobRepository())))
            .CreateClient();

    [Fact]
    public async Task Storage_timeout_returns_503_problem()
    {
        var response = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        problem.Title.Should().Be("Storage unavailable");
    }
}
