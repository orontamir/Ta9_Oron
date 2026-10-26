using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ta9.JobService.Api;
using Ta9.JobService.Tests.TestSupport;
using Ta9.ServiceDefaults;

namespace Ta9.JobService.Tests.Api;

/// <summary>The Job Service's real logging pipeline: what an API call leaves in the log store.</summary>
public sealed class JobsApiLoggingTests(JobServiceFactory factory) : IClassFixture<JobServiceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> CreateJobAsync(string correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        {
            Content = JsonContent.Create(new CreateJobRequest("logged", [new CreateJobDocumentRequest("a.txt", "one two"), new CreateJobDocumentRequest("b.txt", "three")]))
        };
        request.Headers.Add(Correlation.HeaderName, correlationId);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobResponse>(Json))!.Id;
    }

    [Fact]
    public async Task Creating_a_job_stores_info_and_debug_records_with_job_id_and_correlation_id()
    {
        var jobId = await CreateJobAsync("create-logs-1");

        var created = (await factory.Logs.WaitForAsync(r => r.Message == $"Job {jobId} created with 2 documents")).Single();
        created.Level.Should().Be("Information");
        created.Service.Should().Be("job-service");
        created.CorrelationId.Should().Be("create-logs-1");
        created.JobId.Should().Be(jobId.ToString());
        factory.Logs.Records.Should().Contain(r => r.Level == "Debug" && r.Message == $"Stored 2 documents of job {jobId}" && r.CorrelationId == "create-logs-1");
        factory.Logs.Records.Should().Contain(r => r.Message.StartsWith("HTTP POST /api/jobs responded 201") && r.CorrelationId == "create-logs-1");
    }

    [Fact]
    public async Task Invalid_request_is_logged_without_error_level()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs") { Content = JsonContent.Create(new { name = "bad", documents = Array.Empty<object>() }) };
        request.Headers.Add(Correlation.HeaderName, "invalid-logs-1");

        await _client.SendAsync(request);

        var records = await factory.Logs.WaitForAsync(r => r.CorrelationId == "invalid-logs-1" && r.Message.StartsWith("HTTP POST /api/jobs responded 400"));
        records.Single().Level.Should().Be("Information", "a client error is not a service error");
        factory.Logs.Records.Where(r => r.CorrelationId == "invalid-logs-1").Should().NotContain(r => r.Level == "Error");
    }
}
