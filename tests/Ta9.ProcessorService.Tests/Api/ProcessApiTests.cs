using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Domain;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Api;

public sealed class ProcessApiTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private HttpClient CreateClient(IDocumentAnalyzer? analyzer = null, params (string Key, string Value)[] settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            if (analyzer is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton(analyzer));
            }
        });
        _disposables.Add(factory);
        var client = factory.CreateClient();
        _disposables.Add(client);
        return client;
    }

    private static ProcessJobRequest Request(params string[] contents) =>
        new(Guid.NewGuid(), TestData.Documents(contents.Length == 0 ? new[] { "hello world hello" } : contents));

    private static Task<ProcessJobStatusResponse> WaitForStatusAsync(HttpClient client, Guid jobId, ProcessingStatus status) =>
        Eventually.MatchAsync(
            async () => (await client.GetFromJsonAsync<ProcessJobStatusResponse>($"/api/process/{jobId}"))!,
            response => response.Status == status);

    [Fact]
    public async Task Post_accepts_job_and_results_become_available()
    {
        var client = CreateClient();
        var request = Request("hello world hello", "one two");

        var response = await client.PostAsJsonAsync("/api/process", request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().Should().EndWith($"/api/process/{request.JobId}");

        var completed = await WaitForStatusAsync(client, request.JobId, ProcessingStatus.Completed);
        completed.Results.Should().HaveCount(2);
        completed.Results![0].WordCount.Should().Be(3);
        completed.Results[0].TopWords[0].Should().Be(new WordCountDto("hello", 2));
        completed.Summary!.Succeeded.Should().Be(2);
    }

    [Fact]
    public async Task Post_same_job_twice_returns_200_the_second_time()
    {
        var client = CreateClient();
        var request = Request();

        (await client.PostAsJsonAsync("/api/process", request)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var second = await client.PostAsJsonAsync("/api/process", request);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<ProcessJobStatusResponse>())!.JobId.Should().Be(request.JobId);
    }

    [Fact]
    public async Task Post_invalid_request_returns_validation_problem()
    {
        var client = CreateClient();
        var request = new ProcessJobRequest(Guid.Empty, []);

        var response = await client.PostAsJsonAsync("/api/process", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem!.Errors.Should().ContainKey("jobId").And.ContainKey("documents");
    }

    [Fact]
    public async Task Post_malformed_json_returns_400()
    {
        var client = CreateClient();

        var response = await client.PostAsync("/api/process", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_returns_503_with_retry_after_when_queue_is_full()
    {
        using var gate = new ManualResetEventSlim(false);
        var blocking = new DelegateAnalyzer(content =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return DelegateAnalyzer.Real.Analyze(content);
        });
        var client = CreateClient(blocking,
            ("Processing:MaxConcurrentJobs", "1"),
            ("Processing:QueueCapacity", "1"),
            ("Processing:RetryAfterSeconds", "7"));

        try
        {
            var running = Request();
            await client.PostAsJsonAsync("/api/process", running);
            await WaitForStatusAsync(client, running.JobId, ProcessingStatus.Processing);
            (await client.PostAsJsonAsync("/api/process", Request())).StatusCode.Should().Be(HttpStatusCode.Accepted);

            var rejected = await client.PostAsJsonAsync("/api/process", Request());

            rejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            rejected.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(7));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Delete_cancels_a_running_job()
    {
        using var gate = new ManualResetEventSlim(false);
        var blocking = new DelegateAnalyzer(content =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return DelegateAnalyzer.Real.Analyze(content);
        });
        var client = CreateClient(blocking);

        try
        {
            var request = Request("a", "b", "c");
            await client.PostAsJsonAsync("/api/process", request);
            await WaitForStatusAsync(client, request.JobId, ProcessingStatus.Processing);

            var response = await client.DeleteAsync($"/api/process/{request.JobId}");

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await client.GetFromJsonAsync<ProcessJobStatusResponse>($"/api/process/{request.JobId}"))!
                .Status.Should().Be(ProcessingStatus.Cancelled);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Delete_finished_job_returns_409()
    {
        var client = CreateClient();
        var request = Request();
        await client.PostAsJsonAsync("/api/process", request);
        await WaitForStatusAsync(client, request.JobId, ProcessingStatus.Completed);

        var response = await client.DeleteAsync($"/api/process/{request.JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Unknown_job_returns_404(string method)
    {
        var client = CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/process/{Guid.NewGuid()}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task List_returns_known_jobs()
    {
        var client = CreateClient();
        var request = Request();
        await client.PostAsJsonAsync("/api/process", request);

        var jobs = await client.GetFromJsonAsync<List<ProcessJobStatusResponse>>("/api/process");

        jobs.Should().ContainSingle(j => j.JobId == request.JobId);
    }

    [Fact]
    public async Task Liveness_endpoint_is_healthy()
    {
        var client = CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
