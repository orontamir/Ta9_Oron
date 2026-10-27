using System.Net;
using System.Net.Http.Json;
using Ta9.JobService.Api;
using Ta9.JobService.Tests.TestSupport;
using Ta9.Tests.Shared;

namespace Ta9.JobService.Tests.Api;

/// <summary>Who may do what on the Job Service's public API (reader / operator / services).</summary>
public sealed class JobsApiAuthTests(JobServiceFactory factory) : IClassFixture<JobServiceFactory>
{
    private static readonly CreateJobRequest NewJob = new("auth", [new CreateJobDocumentRequest("a.txt", "one two")]);

    private HttpClient Client(string? key) => factory.CreateClient().WithApiKey(key);

    private async Task<Guid> CreateJobAsync()
    {
        var response = await Client(TestApiKeys.Operator).PostAsJsonAsync("/api/jobs", NewJob);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JobIdOnly>())!.Id;
    }

    private sealed record JobIdOnly(Guid Id);

    [Fact]
    public async Task Request_without_a_key_is_401()
    {
        var response = await Client(null).GetAsync("/api/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Reader_can_read_but_cannot_create_cancel_or_delete()
    {
        var jobId = await CreateJobAsync();
        var reader = Client(TestApiKeys.Reader);

        (await reader.GetAsync("/api/jobs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync($"/api/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.PostAsJsonAsync("/api/jobs", NewJob)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.PostAsync($"/api/jobs/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync($"/api/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Processor_key_cannot_use_the_client_api()
    {
        (await Client(TestApiKeys.ProcessorService).GetAsync("/api/jobs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Job_service_key_is_not_accepted_by_the_job_service()
    {
        (await Client(TestApiKeys.JobService).GetAsync("/api/jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Live_status_stream_requires_a_key()
    {
        var jobId = await CreateJobAsync();

        (await Client(null).GetAsync($"/api/jobs/{jobId}/events")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_endpoints_are_anonymous()
    {
        (await Client(null).GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client(null).GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
