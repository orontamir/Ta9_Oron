using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ta9.ServiceDefaults.Logging;
using Ta9.Tests.Shared;

namespace Ta9.ServiceDefaults.Tests.Logging;

/// <summary>
/// The real hosting setup (<see cref="ServiceDefaultsExtensions.AddServiceDefaults"/>): Serilog → level filters →
/// <see cref="BatchingLogSink"/> → writer. Only the database is replaced by <see cref="CapturingLogWriter"/>.
/// </summary>
public class ServiceLoggingPipelineTests
{
    private const string Service = "test-service";

    private static async Task<(WebApplication App, HttpClient Client, CapturingLogWriter Logs)> StartAsync(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Debug",
            ["Logging:UseJsonConsole"] = "false",
            ["Logging:Mongo:Enabled"] = "true",
            ["Logging:Mongo:FlushInterval"] = "00:00:00.050"
        });
        builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        builder.AddServiceDefaults(Service);
        var logs = new CapturingLogWriter();
        builder.Services.AddSingleton<ILogBatchWriter>(logs);

        var app = builder.Build();
        app.UseServiceDefaults();
        app.MapGet("/work", (ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("Ta9.Tests.Work");
            logger.LogDebug("debug detail {Step}", 1);
            logger.LogInformation("flow step {Step}", 2);
            logger.LogError(new InvalidOperationException("boom"), "something failed at {Step}", 3);
            return "ok";
        });
        app.MapGet("/crash", IResult () => throw new InvalidOperationException("unhandled in endpoint"));
        await app.StartAsync();
        return (app, app.GetTestClient(), logs);
    }

    private static async Task GetAsync(HttpClient client, string path, string correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(Correlation.HeaderName, correlationId);
        using var _ = await client.SendAsync(request);
    }

    [Fact]
    public async Task Debug_information_and_error_are_stored_with_service_and_correlation_id()
    {
        var (app, client, logs) = await StartAsync();
        await using (app)
        {
            await GetAsync(client, "/work", "corr-77");

            await logs.WaitForAsync(r => r.Message.StartsWith("HTTP GET /work"));
            var work = logs.Records.Where(r => r.SourceContext == "Ta9.Tests.Work").ToList();
            work.Select(r => r.Level).Should().Equal("Debug", "Information", "Error");
            work.Should().OnlyContain(r => r.Service == Service && r.CorrelationId == "corr-77");
            work.Single(r => r.Level == "Error").Exception.Should().Contain("boom");
            logs.Records.Single(r => r.Message.StartsWith("HTTP GET /work")).CorrelationId.Should().Be("corr-77", "the request log carries the id too");
        }
    }

    [Fact]
    public async Task Mongo_minimum_level_filters_what_is_stored()
    {
        var (app, client, logs) = await StartAsync(("Logging:Mongo:MinimumLevel", "Information"));
        await using (app)
        {
            await GetAsync(client, "/work", "corr-1");

            await logs.WaitForAsync(r => r.Level == "Error" && r.SourceContext == "Ta9.Tests.Work");
            logs.Records.Should().NotContain(r => r.Level == "Debug");
            logs.Records.Should().Contain(r => r.Message == "flow step 2");
        }
    }

    [Fact]
    public async Task Unhandled_endpoint_exception_is_stored_as_an_error_with_the_stack_trace()
    {
        var (app, client, logs) = await StartAsync();
        await using (app)
        {
            await GetAsync(client, "/crash", "corr-crash");

            var errors = await logs.WaitForAsync(r => r.Level == "Error" && r.Exception?.Contains("unhandled in endpoint") == true);
            errors.Should().Contain(r => r.CorrelationId == "corr-crash");
        }
    }

    [Fact]
    public async Task Startup_logs_where_logs_go_and_that_the_service_started()
    {
        var (app, _, logs) = await StartAsync();
        await using (app)
        {
            await logs.WaitForAsync(r => r.Message.StartsWith("Service started"));
            logs.Records.Should().Contain(r => r.Message.StartsWith("Logging: console >= Information; MongoDB ta9_logs.logs >= Debug"));
        }
    }

    [Fact]
    public async Task Disabled_mongo_logging_stores_nothing()
    {
        var (app, client, logs) = await StartAsync(("Logging:Mongo:Enabled", "false"));
        await using (app)
        {
            await GetAsync(client, "/work", "corr-2");
            await Task.Delay(200);

            logs.Records.Should().BeEmpty();
            app.Services.GetRequiredService<LogSinkHolder>().Sink.Should().BeNull();
        }
    }

    [Fact]
    public async Task Stopping_the_host_flushes_buffered_records()
    {
        var (app, client, logs) = await StartAsync(("Logging:Mongo:FlushInterval", "01:00:00"));
        await GetAsync(client, "/work", "corr-3");

        await app.StopAsync();
        await app.DisposeAsync();

        logs.Records.Should().Contain(r => r.Message == "flow step 2").And.Contain(r => r.Message == "Service stopping");
    }
}
