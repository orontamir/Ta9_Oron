using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ta9.JobService.Api;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Audit;
using Ta9.JobService.Infrastructure.Processor;
using Ta9.ProcessorService.Domain;
using Ta9.ProcessorService.Infrastructure.Events;
using AuditProgram = Ta9.AuditService.Program;
using JobProgram = Ta9.JobService.Program;
using ProcessorProgram = Ta9.ProcessorService.Program;

namespace Ta9.IntegrationTests.Infrastructure;

public sealed class ConfigurableFactory<TEntryPoint>(Action<IWebHostBuilder> configure) : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);
}

public sealed record SystemOptions
{
    /// <summary>Replaces the Processor's document analyzer (e.g. to block, slow down or count calls).</summary>
    public IDocumentAnalyzer? Analyzer { get; init; }

    /// <summary>False: the Job Service gets connection errors for every call to the Processor.</summary>
    public bool ProcessorReachable { get; init; } = true;

    public int MaxDispatchAttempts { get; init; } = 5;

    /// <summary>Short shutdown so a "crashed" processor stuck on a blocked analyzer is torn down quickly.</summary>
    public TimeSpan? ProcessorShutdownTimeout { get; init; }
}

/// <summary>
/// The three services running in-process on TestServer and wired together exactly as in production:
/// Job Service → Processor over REST (real HttpClient pipeline), Processor → Job Service over a real WebSocket,
/// Job Service → Audit over REST, MongoDB for storage. Only the network "cable" is replaced.
/// </summary>
public sealed class Ta9System : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _mongoConnectionString;
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly SystemOptions _options;
    private ConfigurableFactory<ProcessorProgram>? _processor;

    private Ta9System(string mongoConnectionString, SystemOptions options)
    {
        _mongoConnectionString = mongoConnectionString;
        _options = options;
    }

    public ConfigurableFactory<JobProgram> Jobs { get; private set; } = null!;
    public ConfigurableFactory<AuditProgram> Audit { get; private set; } = null!;
    public HttpClient JobsApi { get; private set; } = null!;
    public HttpClient AuditApi { get; private set; } = null!;
    public HttpClient ProcessorApi { get; private set; } = null!;

    public static Ta9System Start(string mongoConnectionString, SystemOptions? options = null)
    {
        var system = new Ta9System(mongoConnectionString, options ?? new SystemOptions());
        system.StartAll();
        return system;
    }

    private void StartAll()
    {
        Audit = new ConfigurableFactory<AuditProgram>(builder =>
        {
            builder.UseSetting("Storage:ConnectionString", _mongoConnectionString);
            builder.UseSetting("Storage:Database", $"e2e_audit_{_suffix}");
        });
        AuditApi = Audit.CreateClient();

        Jobs = new ConfigurableFactory<JobProgram>(builder =>
        {
            builder.UseSetting("Storage:Mongo:ConnectionString", _mongoConnectionString);
            builder.UseSetting("Storage:Mongo:Database", $"e2e_jobs_{_suffix}");
            builder.UseSetting("Dispatch:PollInterval", "00:00:00.200");
            builder.UseSetting("Dispatch:InitialBackoff", "00:00:00.200");
            builder.UseSetting("Dispatch:MaxBackoff", "00:00:01");
            builder.UseSetting("Dispatch:MaxDispatchAttempts", _options.MaxDispatchAttempts.ToString());
            builder.UseSetting("Reconcile:Interval", "00:00:01");
            builder.UseSetting("Reconcile:StaleAfter", "00:00:02");
            builder.UseSetting("Audit:Interval", "00:00:00.500");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(HttpProcessorClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new RoutingHandler(CurrentProcessorHandler));
                services.AddHttpClient(HttpAuditClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new RoutingHandler(() => Audit.Server.CreateHandler()));
            });
        });
        JobsApi = Jobs.CreateClient();

        if (_options.ProcessorReachable)
        {
            _processor = CreateProcessor(_options.Analyzer, _options.ProcessorShutdownTimeout);
            ProcessorApi = _processor.CreateClient();
        }
    }

    /// <summary>Simulates a Processor crash/restart: a fresh instance (empty memory) replaces the old one.</summary>
    public async Task RestartProcessorAsync(IDocumentAnalyzer? analyzer = null)
    {
        var old = _processor;
        var oldApi = ProcessorApi;

        _processor = CreateProcessor(analyzer, shutdownTimeout: null);
        ProcessorApi = _processor.CreateClient(); // starts the new instance before the old one goes away

        oldApi.Dispose();
        if (old is not null)
        {
            await old.DisposeAsync();
        }
    }

    public async Task<JobResponse> CreateJobAsync(IReadOnlyList<string> contents, string name = "e2e")
    {
        var request = new CreateJobRequest(name, contents.Select((c, i) => new CreateJobDocumentRequest($"doc-{i}.txt", c)).ToList());
        var response = await JobsApi.PostAsJsonAsync("/api/jobs", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobResponse>(Json))!;
    }

    public async Task<JobResponse> GetJobAsync(Guid jobId) =>
        (await JobsApi.GetFromJsonAsync<JobResponse>($"/api/jobs/{jobId}", Json))!;

    public async Task<JobResponse> WaitForStatusAsync(Guid jobId, TimeSpan timeout, params JobStatus[] statuses)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var job = await GetJobAsync(jobId);
            if (statuses.Contains(job.Status))
            {
                return job;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} is {job.Status} ({job.FailureReason}); expected {string.Join("/", statuses)}.");
            }

            await Task.Delay(100);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ProcessorApi?.Dispose();
        if (_processor is not null)
        {
            await _processor.DisposeAsync();
        }

        JobsApi.Dispose();
        await Jobs.DisposeAsync();
        AuditApi.Dispose();
        await Audit.DisposeAsync();
    }

    private HttpMessageHandler CurrentProcessorHandler() =>
        _processor is null ? new UnreachableHandler() : _processor.Server.CreateHandler();

    private ConfigurableFactory<ProcessorProgram> CreateProcessor(IDocumentAnalyzer? analyzer, TimeSpan? shutdownTimeout) =>
        new(builder =>
        {
            builder.UseSetting("JobServiceEvents:ReconnectInitialDelay", "00:00:00.100");
            builder.UseSetting("JobServiceEvents:ReconnectMaxDelay", "00:00:01");
            builder.UseSetting("JobServiceEvents:AckTimeout", "00:00:05");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IWebSocketConnector>(new JobServiceTestConnector(() => Jobs.Server));
                if (analyzer is not null)
                {
                    services.AddSingleton(analyzer);
                }

                if (shutdownTimeout is { } timeout)
                {
                    services.Configure<HostOptions>(o => o.ShutdownTimeout = timeout);
                }
            });
        });

    /// <summary>Forwards each request to whatever handler is current (so a restarted Processor is picked up).</summary>
    private sealed class RoutingHandler(Func<HttpMessageHandler> current) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new HttpMessageInvoker(current(), disposeHandler: false).SendAsync(request, cancellationToken);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused (simulated: processor is down)");
    }

    /// <summary>Opens the Processor → Job Service WebSocket on the Job Service's TestServer.</summary>
    private sealed class JobServiceTestConnector(Func<TestServer> jobService) : IWebSocketConnector
    {
        public Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
            jobService().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/processor-events"), cancellationToken);
    }
}
