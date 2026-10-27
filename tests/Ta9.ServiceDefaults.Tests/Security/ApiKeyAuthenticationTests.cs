using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ta9.ServiceDefaults.Logging;
using Ta9.ServiceDefaults.Security;
using Ta9.Tests.Shared;

namespace Ta9.ServiceDefaults.Tests.Security;

/// <summary>The real hosting setup (AddServiceDefaults + AddApiKeyAuthentication + UseServiceDefaults) on TestServer.</summary>
public class ApiKeyAuthenticationTests
{
    private static async Task<(WebApplication App, CapturingLogWriter Logs)> StartAsync(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Debug",
            ["Logging:UseJsonConsole"] = "false",
            ["Logging:Mongo:Enabled"] = "true",
            ["Logging:Mongo:FlushInterval"] = "00:00:00.050",
            ["Auth:ReaderKey"] = TestApiKeys.Reader,
            ["Auth:OperatorKey"] = TestApiKeys.Operator,
            ["Auth:JobServiceKey"] = TestApiKeys.JobService
        });
        builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        builder.AddServiceDefaults("auth-test");
        builder.AddApiKeyAuthentication(Caller.Reader, Caller.Operator, Caller.JobService);
        var logs = new CapturingLogWriter();
        builder.Services.AddSingleton<ILogBatchWriter>(logs);

        var app = builder.Build();
        app.UseServiceDefaults();
        app.MapGet("/read", () => "read").RequireAuthorization(AuthPolicies.Read);
        app.MapPost("/write", () => "written").RequireAuthorization(AuthPolicies.Write);
        app.MapPost("/service", () => "service").RequireAuthorization(AuthPolicies.Service);
        app.MapGet("/default", () => "default");
        await app.StartAsync();
        return (app, logs);
    }

    private static HttpClient Client(WebApplication app, string? key) => app.GetTestClient().WithApiKey(key);

    [Fact]
    public async Task Missing_key_returns_401_problem_with_a_challenge_header()
    {
        var (app, _) = await StartAsync();
        await using (app)
        {
            var response = await Client(app, null).GetAsync("/read");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ToString().Should().Contain("X-Api-Key");
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        }
    }

    [Fact]
    public async Task Invalid_key_returns_401_and_is_logged_without_the_key()
    {
        var (app, logs) = await StartAsync();
        await using (app)
        {
            const string bogus = "bogus-key-that-must-never-appear-in-logs";

            (await Client(app, bogus).GetAsync("/read")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var warning = (await logs.WaitForAsync(r => r.Message == "Rejected GET /read: invalid API key")).Single();
            warning.Level.Should().Be("Warning");
            warning.CorrelationId.Should().NotBeNull();
            logs.Records.Should().NotContain(r => r.Message.Contains(bogus) || r.Properties.Values.Any(v => v.Contains(bogus)));
        }
    }

    [Fact]
    public async Task Reader_can_read_but_not_write()
    {
        var (app, _) = await StartAsync();
        await using (app)
        {
            var reader = Client(app, TestApiKeys.Reader);

            (await reader.GetAsync("/read")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await reader.PostAsync("/write", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Operator_can_read_and_write()
    {
        var (app, _) = await StartAsync();
        await using (app)
        {
            var @operator = Client(app, TestApiKeys.Operator);

            (await @operator.GetAsync("/read")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await @operator.PostAsync("/write", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Client_and_service_keys_are_kept_apart()
    {
        var (app, _) = await StartAsync();
        await using (app)
        {
            (await Client(app, TestApiKeys.Operator).PostAsync("/service", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Client(app, TestApiKeys.JobService).PostAsync("/service", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Client(app, TestApiKeys.JobService).GetAsync("/read")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Key_of_a_caller_this_service_does_not_trust_is_rejected()
    {
        var (app, _) = await StartAsync(("Auth:ProcessorServiceKey", TestApiKeys.ProcessorService));
        await using (app)
        {
            (await Client(app, TestApiKeys.ProcessorService).PostAsync("/service", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Endpoint_without_a_policy_still_requires_a_key()
    {
        var (app, _) = await StartAsync();
        await using (app)
        {
            (await Client(app, null).GetAsync("/default")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await Client(app, TestApiKeys.Reader).GetAsync("/default")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Health_endpoints_are_anonymous()
    {
        var (app, _) = await StartAsync();
        await using (app)
        {
            (await Client(app, null).GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Client(app, null).GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Request_log_line_records_the_caller()
    {
        var (app, logs) = await StartAsync();
        await using (app)
        {
            await Client(app, TestApiKeys.Operator).PostAsync("/write", null);

            var line = (await logs.WaitForAsync(r => r.Message.StartsWith("HTTP POST /write responded 200"))).Single();
            line.Properties["Caller"].Should().Be("operator");
        }
    }

    [Fact]
    public async Task Disabled_authentication_allows_every_request_and_warns()
    {
        var (app, logs) = await StartAsync(("Auth:Enabled", "false"));
        await using (app)
        {
            (await Client(app, null).PostAsync("/service", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await logs.WaitForAsync(r => r.Message.StartsWith("Authentication is DISABLED"))).Single().Level.Should().Be("Warning");
        }
    }

    [Fact]
    public async Task Startup_lists_accepted_callers_and_warns_about_missing_keys()
    {
        var (app, logs) = await StartAsync(("Auth:ReaderKey", ""));
        await using (app)
        {
            await logs.WaitForAsync(r => r.Message == "Authentication: API key (X-Api-Key) required; accepted callers: reader, operator, job-service");
            (await logs.WaitForAsync(r => r.Message.StartsWith("No API key configured for reader"))).Single().Level.Should().Be("Warning");
        }
    }
}
