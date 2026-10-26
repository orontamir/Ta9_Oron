using System.Net.Http.Json;
using Ta9.Contracts.Audit;
using Ta9.JobService.Application;

namespace Ta9.JobService.Infrastructure.Audit;

public sealed class HttpAuditClient(IHttpClientFactory httpClientFactory, ILogger<HttpAuditClient> logger) : IAuditClient
{
    public const string HttpClientName = "audit";

    public async Task<AuditPublishOutcome> PublishAsync(JobAuditRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsJsonAsync("api/audit", request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return AuditPublishOutcome.Recorded;
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                logger.LogError("Audit service returned {Status} for job {JobId}: {Body}",
                    (int)response.StatusCode, request.JobId, await response.Content.ReadAsStringAsync(cancellationToken));
                return AuditPublishOutcome.Rejected;
            }

            return AuditPublishOutcome.Unavailable;
        }
        catch (HttpRequestException)
        {
            return AuditPublishOutcome.Unavailable;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuditPublishOutcome.Unavailable;
        }
    }
}
