using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ta9.ServiceDefaults.Security;

namespace Ta9.ServiceDefaults.Tests.Security;

public class ApiKeyHeaderHandlerTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static async Task<HttpRequestMessage> SendThroughClientAsync(string? jobServiceKey)
    {
        var inner = new CapturingHandler();
        var services = new ServiceCollection();
        services.AddOptions<ApiKeyOptions>().Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:JobServiceKey"] = jobServiceKey })
            .Build().GetSection(ApiKeyOptions.SectionName));
        services.AddHttpClient("downstream", c => c.BaseAddress = new Uri("http://downstream/"))
            .ConfigurePrimaryHttpMessageHandler(() => inner)
            .AddApiKey(keys => keys.JobServiceKey);

        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IHttpClientFactory>().CreateClient("downstream").GetAsync("api/process");
        return inner.Last!;
    }

    [Fact]
    public async Task Adds_the_service_key_to_outgoing_requests()
    {
        var request = await SendThroughClientAsync("job-service-key-0123456789abcdef0123");

        request.Headers.GetValues(ApiKeyAuthenticationHandler.HeaderName).Should().Equal("job-service-key-0123456789abcdef0123");
    }

    [Fact]
    public async Task Sends_no_header_when_no_key_is_configured()
    {
        var request = await SendThroughClientAsync(null);

        request.Headers.Contains(ApiKeyAuthenticationHandler.HeaderName).Should().BeFalse();
    }
}
