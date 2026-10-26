using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.JobService.Application;

namespace Ta9.JobService.Infrastructure.Processor;

public sealed class ProcessorClientOptions
{
    public const string SectionName = "Processor";

    [Required]
    public Uri BaseUrl { get; set; } = new("http://localhost:5002/");

    /// <summary>Generous: a dispatch can carry up to ~10 MB of documents.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cancel is best effort and must not hold up the client's cancel request.</summary>
    public TimeSpan CancelTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// REST client for /api/process. Translates HTTP statuses and transport exceptions into outcomes the
/// dispatcher and reconciler can act on; only caller cancellation escapes as an exception.
/// </summary>
public sealed class HttpProcessorClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ProcessorClientOptions> options,
    ILogger<HttpProcessorClient> logger) : IProcessorClient
{
    public const string HttpClientName = "processor";
    private const int MaxErrorBodyLength = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DispatchResult> SubmitAsync(Guid jobId, IReadOnlyList<ProcessDocumentRequest> documents, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client().PostAsJsonAsync("api/process", new ProcessJobRequest(jobId, documents), Json, cancellationToken);
            logger.LogDebug("POST api/process for job {JobId} returned {StatusCode}", jobId, (int)response.StatusCode);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Accepted or HttpStatusCode.OK:
                    return DispatchResult.Accepted();
                case HttpStatusCode.ServiceUnavailable:
                    return DispatchResult.Busy(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5));
            }

            var error = $"{(int)response.StatusCode} {await ReadSnippetAsync(response, cancellationToken)}";
            return (int)response.StatusCode is >= 400 and < 500
                ? DispatchResult.Rejected(error)
                : DispatchResult.Unreachable(error);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "POST api/process for job {JobId} failed: processor unreachable", jobId);
            return DispatchResult.Unreachable(ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DispatchResult.Unreachable($"Timed out after {options.Value.RequestTimeout.TotalSeconds:0}s");
        }
    }

    public async Task<ProcessorLookup> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client().GetAsync($"api/process/{jobId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ProcessorLookup(LookupOutcome.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProcessorLookup(LookupOutcome.Unreachable, Error: $"{(int)response.StatusCode}");
            }

            var status = await response.Content.ReadFromJsonAsync<ProcessJobStatusResponse>(Json, cancellationToken);
            return new ProcessorLookup(LookupOutcome.Found, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ProcessorLookup(LookupOutcome.Unreachable, Error: ex.Message);
        }
    }

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.CancelTimeout);
        try
        {
            using var response = await Client().DeleteAsync($"api/process/{jobId}", timeout.Token);
            // 404: the processor never had it or already evicted it; 409: it already finished. Both are fine.
            return response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not cancel job {JobId} on the processor: {Error}", jobId, ex.Message);
            return false;
        }
    }

    private HttpClient Client() => httpClientFactory.CreateClient(HttpClientName);

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Length <= MaxErrorBodyLength ? body : body[..MaxErrorBodyLength];
    }
}
