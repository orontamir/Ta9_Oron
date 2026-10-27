using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Ta9.ServiceDefaults.Security;

namespace Ta9.Tests.Shared;

/// <summary>
/// API keys for test hosts, generated randomly for each test run: no key is ever written in a file.
/// </summary>
public static class TestApiKeys
{
    public static readonly string Reader = New();
    public static readonly string Operator = New();
    public static readonly string JobService = New();
    public static readonly string ProcessorService = New();

    /// <summary>Configures every key on a test host (each service still accepts only its own callers).</summary>
    public static void Apply(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:ReaderKey", Reader);
        builder.UseSetting("Auth:OperatorKey", Operator);
        builder.UseSetting("Auth:JobServiceKey", JobService);
        builder.UseSetting("Auth:ProcessorServiceKey", ProcessorService);
    }

    public static HttpClient WithApiKey(this HttpClient client, string? key)
    {
        client.DefaultRequestHeaders.Remove(ApiKeyAuthenticationHandler.HeaderName);
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
        }

        return client;
    }

    private static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

/// <summary>Sends the key that fits the request: one key for writes, another for reads (e.g. Audit: service writes, clients read).</summary>
public sealed class KeyByMethodHandler(string writeKey, string readKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(ApiKeyAuthenticationHandler.HeaderName);
        request.Headers.Add(ApiKeyAuthenticationHandler.HeaderName, request.Method == HttpMethod.Get ? readKey : writeKey);
        return base.SendAsync(request, cancellationToken);
    }
}
