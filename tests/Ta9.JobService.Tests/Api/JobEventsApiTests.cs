using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ta9.JobService.Api;
using Ta9.JobService.Domain;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Api;

/// <summary>End-to-end SSE: a real streaming HTTP response read line by line.</summary>
public sealed class JobEventsApiTests : IClassFixture<JobServiceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly JobServiceFactory _factory;
    private readonly HttpClient _client;

    public JobEventsApiTests(JobServiceFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record SseEvent(string Event, string? Id, JobResponse Data);

    private async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenAsync(Guid jobId)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{jobId}/events"), HttpCompletionOption.ResponseHeadersRead);
        return (response, new StreamReader(await response.Content.ReadAsStreamAsync()));
    }

    /// <summary>Next SSE event, or null when the server ended the stream.</summary>
    private static async Task<SseEvent?> ReadEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? name = null, id = null, data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                if (data is not null)
                {
                    return new SseEvent(name ?? "message", id, JsonSerializer.Deserialize<JobResponse>(data, Json)!);
                }
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue; // heartbeat comment
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line["event: ".Length..];
            else if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line["id: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line["data: ".Length..];
        }
    }

    private async Task<Job> InsertAsync(JobStatus status)
    {
        var job = TestJobs.InStatus(status);
        await _factory.Jobs.InsertAsync(job, CancellationToken.None);
        return job;
    }

    [Fact]
    public async Task Streams_snapshot_then_every_change_until_the_job_finishes()
    {
        var job = await InsertAsync(JobStatus.Pending);
        var (response, reader) = await OpenAsync(job.Id);
        using (response)
        using (reader)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

            var snapshot = await ReadEventAsync(reader);
            snapshot!.Event.Should().Be("status");
            snapshot.Data.Status.Should().Be(JobStatus.Pending);

            var now = DateTimeOffset.UtcNow;
            await _factory.SetStatusAsync(job.Id, j => j.TryMarkQueued(now));
            (await ReadEventAsync(reader))!.Data.Status.Should().Be(JobStatus.Queued);

            await _factory.SetStatusAsync(job.Id, j => j.RecordProgress(2, now));
            var progress = await ReadEventAsync(reader);
            progress!.Data.Status.Should().Be(JobStatus.Processing);
            progress.Data.ProcessedDocuments.Should().Be(2);

            await _factory.SetStatusAsync(job.Id, j => j.TryComplete(TestJobs.Summary(), now));
            var completed = await ReadEventAsync(reader);
            completed!.Data.Status.Should().Be(JobStatus.Completed);
            completed.Data.ProgressPercent.Should().Be(100);

            (await ReadEventAsync(reader)).Should().BeNull("the stream ends with the terminal state");
        }
    }

    [Fact]
    public async Task Finished_job_returns_one_snapshot_and_closes()
    {
        var job = await InsertAsync(JobStatus.Cancelled);
        var (response, reader) = await OpenAsync(job.Id);
        using (response)
        using (reader)
        {
            (await ReadEventAsync(reader))!.Data.Status.Should().Be(JobStatus.Cancelled);
            (await ReadEventAsync(reader)).Should().BeNull();
        }
    }

    [Fact]
    public async Task Event_ids_follow_the_job_version()
    {
        var job = await InsertAsync(JobStatus.Queued);
        var (response, reader) = await OpenAsync(job.Id);
        using (response)
        using (reader)
        {
            var first = await ReadEventAsync(reader);
            await _factory.SetStatusAsync(job.Id, j => j.TryCancel(DateTimeOffset.UtcNow));
            var second = await ReadEventAsync(reader);

            long.Parse(second!.Id!).Should().BeGreaterThan(long.Parse(first!.Id!));
        }
    }

    [Fact]
    public async Task Unknown_job_is_404()
    {
        var response = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}/events");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
