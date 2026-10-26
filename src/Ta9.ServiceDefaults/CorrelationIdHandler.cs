namespace Ta9.ServiceDefaults;

/// <summary>Propagates the ambient correlation id on outgoing HttpClient calls.</summary>
public sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = Correlation.CurrentId;
        if (correlationId is not null && !request.Headers.Contains(Correlation.HeaderName))
        {
            request.Headers.Add(Correlation.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
