using System.Net;

namespace Ta9.ServiceDefaults.Tests;

public class CorrelationIdHandlerTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, CapturingHandler Inner) CreateClient()
    {
        var inner = new CapturingHandler();
        var client = new HttpClient(new CorrelationIdHandler { InnerHandler = inner });
        return (client, inner);
    }

    [Fact]
    public async Task Adds_header_from_ambient_scope()
    {
        var (client, inner) = CreateClient();

        using (Correlation.BeginScope("ambient-1"))
        {
            await client.GetAsync("http://localhost/");
        }

        inner.Captured!.Headers.GetValues(Correlation.HeaderName).Should().ContainSingle().Which.Should().Be("ambient-1");
    }

    [Fact]
    public async Task Does_not_add_header_without_scope()
    {
        var (client, inner) = CreateClient();

        await client.GetAsync("http://localhost/");

        inner.Captured!.Headers.Contains(Correlation.HeaderName).Should().BeFalse();
    }

    [Fact]
    public async Task Keeps_explicit_header()
    {
        var (client, inner) = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/");
        request.Headers.Add(Correlation.HeaderName, "explicit");

        using (Correlation.BeginScope("ambient"))
        {
            await client.SendAsync(request);
        }

        inner.Captured!.Headers.GetValues(Correlation.HeaderName).Should().ContainSingle().Which.Should().Be("explicit");
    }
}
