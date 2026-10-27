namespace Ta9.ServiceDefaults.Security;

/// <summary>Adds this service's own API key to outgoing requests of a typed/named HttpClient.</summary>
public sealed class ApiKeyHeaderHandler(Func<string?> key) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var value = key();
        if (!string.IsNullOrEmpty(value) && !request.Headers.Contains(ApiKeyAuthenticationHandler.HeaderName))
        {
            request.Headers.Add(ApiKeyAuthenticationHandler.HeaderName, value);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
