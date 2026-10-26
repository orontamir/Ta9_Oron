using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Ta9.ServiceDefaults.Tests;

public class CorrelationIdMiddlewareTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseMiddleware<CorrelationIdMiddleware>();
        _app.MapGet("/echo", () => Results.Text(Correlation.CurrentId ?? "none"));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Generates_id_when_header_missing_and_echoes_it()
    {
        var response = await _client.GetAsync("/echo");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBe("none");
        Correlation.IsValid(body).Should().BeTrue();
        response.Headers.GetValues(Correlation.HeaderName).Should().ContainSingle().Which.Should().Be(body);
    }

    [Fact]
    public async Task Reuses_valid_incoming_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/echo");
        request.Headers.Add(Correlation.HeaderName, "client-id-42");

        var response = await _client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be("client-id-42");
        response.Headers.GetValues(Correlation.HeaderName).Should().ContainSingle().Which.Should().Be("client-id-42");
    }

    [Fact]
    public async Task Replaces_invalid_incoming_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/echo");
        request.Headers.TryAddWithoutValidation(Correlation.HeaderName, "bad id with spaces");

        var response = await _client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBe("bad id with spaces");
        Correlation.IsValid(body).Should().BeTrue();
    }
}
