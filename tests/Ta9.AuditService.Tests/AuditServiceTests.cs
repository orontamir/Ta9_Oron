using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;
using Ta9.AuditService.Application;
using Ta9.AuditService.Domain;
using Ta9.AuditService.Infrastructure;
using Ta9.Contracts.Audit;
using Ta9.Tests.Shared;
using Testcontainers.MongoDb;

namespace Ta9.AuditService.Tests;

internal static class AuditTestData
{
    public static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).AddMilliseconds(123);

    public static JobAuditRequest Request(Guid? jobId = null, string status = AuditJobStatuses.Completed) => new(
        jobId ?? Guid.NewGuid(), "quarterly", status, 10, 9, 1, 1234,
        status == AuditJobStatuses.Failed ? "Processor unreachable" : null,
        T0, T0.AddSeconds(42));
}

public sealed class AuditApiFactory : WebApplicationFactory<Program>
{
    public CapturingLogWriter Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:Provider", "InMemory");
        Logs.AttachTo(builder);
        TestApiKeys.Apply(builder);
    }

    /// <summary>POSTs as the Job Service (the only writer), GETs as a reader client.</summary>
    public HttpClient CreateAuthorizedClient() =>
        CreateDefaultClient(new KeyByMethodHandler(writeKey: TestApiKeys.JobService, readKey: TestApiKeys.Reader));
}

/// <summary>Only the Job Service records entries; clients read them.</summary>
public sealed class AuditApiAuthTests(AuditApiFactory factory) : IClassFixture<AuditApiFactory>
{
    private HttpClient Client(string? key) => factory.CreateClient().WithApiKey(key);

    [Fact]
    public async Task Request_without_a_key_is_401()
    {
        (await Client(null).PostAsJsonAsync("/api/audit", AuditTestData.Request())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(null).GetAsync($"/api/audit/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Clients_cannot_record_entries()
    {
        (await Client(TestApiKeys.Operator).PostAsJsonAsync("/api/audit", AuditTestData.Request())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Job_service_records_and_a_reader_reads_but_not_the_other_way_round()
    {
        var request = AuditTestData.Request();

        (await Client(TestApiKeys.JobService).PostAsJsonAsync("/api/audit", request)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Client(TestApiKeys.Reader).GetAsync($"/api/audit/{request.JobId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client(TestApiKeys.JobService).GetAsync($"/api/audit/{request.JobId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

/// <summary>The Audit Service's real logging pipeline: what recording a summary leaves in the log store.</summary>
public sealed class AuditApiLoggingTests(AuditApiFactory factory) : IClassFixture<AuditApiFactory>
{
    private readonly HttpClient _client = factory.CreateAuthorizedClient();

    [Fact]
    public async Task Recording_logs_information_and_a_duplicate_logs_debug()
    {
        var request = AuditTestData.Request();

        await _client.PostAsJsonAsync("/api/audit", request);
        await _client.PostAsJsonAsync("/api/audit", request);

        var recorded = (await factory.Logs.WaitForAsync(r => r.Message.StartsWith($"Recorded audit entry for job {request.JobId}"))).Single();
        recorded.Level.Should().Be("Information");
        recorded.Service.Should().Be("audit-service");
        recorded.JobId.Should().Be(request.JobId.ToString());
        (await factory.Logs.WaitForAsync(r => r.Message == $"Audit entry for job {request.JobId} already exists; keeping the first one"))
            .Single().Level.Should().Be("Debug");
    }
}

public sealed class AuditApiTests(AuditApiFactory factory) : IClassFixture<AuditApiFactory>
{
    private readonly HttpClient _client = factory.CreateAuthorizedClient();

    [Fact]
    public async Task Records_a_summary_and_returns_it()
    {
        var request = AuditTestData.Request();

        var response = await _client.PostAsJsonAsync("/api/audit", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().EndWith($"/api/audit/{request.JobId}");
        var stored = await _client.GetFromJsonAsync<JobAuditResponse>($"/api/audit/{request.JobId}");
        stored.Should().BeEquivalentTo(request);
        stored!.RecordedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Recording_twice_is_idempotent_and_keeps_the_first_entry()
    {
        var request = AuditTestData.Request();
        await _client.PostAsJsonAsync("/api/audit", request);

        var second = await _client.PostAsJsonAsync("/api/audit", request with { TotalWords = 999_999 });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JobAuditResponse>())!.TotalWords.Should().Be(request.TotalWords);
    }

    [Fact]
    public async Task Unknown_job_is_404() =>
        (await _client.GetAsync($"/api/audit/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Invalid_summary_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/audit", AuditTestData.Request() with { JobId = Guid.Empty, Status = "Processing" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem!.Errors.Should().ContainKey("jobId").And.ContainKey("status");
    }

    [Fact]
    public async Task Readiness_reports_storage() =>
        (await _client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
}

public class JobAuditRequestValidatorTests
{
    [Theory]
    [InlineData(AuditJobStatuses.Completed)]
    [InlineData(AuditJobStatuses.Failed)]
    [InlineData(AuditJobStatuses.Cancelled)]
    public void Accepts_terminal_statuses(string status) =>
        JobAuditRequestValidator.Validate(AuditTestData.Request(status: status)).Should().BeEmpty();

    [Theory]
    [InlineData("Pending")]
    [InlineData("Processing")]
    [InlineData("")]
    public void Rejects_non_terminal_status(string status) =>
        JobAuditRequestValidator.Validate(AuditTestData.Request(status: status)).Should().ContainKey("status");

    [Fact]
    public void Rejects_inconsistent_counts() =>
        JobAuditRequestValidator.Validate(AuditTestData.Request() with { SucceededDocuments = 10, FailedDocuments = 5 })
            .Should().ContainKey("counts");

    [Fact]
    public void Rejects_completion_before_creation() =>
        JobAuditRequestValidator.Validate(AuditTestData.Request() with { CompletedAt = AuditTestData.T0.AddSeconds(-1) })
            .Should().ContainKey("completedAt");
}

/// <summary>Same behavior for every <see cref="IAuditRepository"/>.</summary>
public abstract class AuditRepositoryContract
{
    protected abstract IAuditRepository Repository { get; }

    [Fact]
    public async Task First_write_wins()
    {
        var entry = AuditEntry.From(AuditTestData.Request(), AuditTestData.T0);
        var duplicate = AuditEntry.From(AuditTestData.Request(entry.JobId) with { TotalWords = 1 }, AuditTestData.T0.AddMinutes(1));

        (await Repository.TryInsertAsync(entry, CancellationToken.None)).Should().BeTrue();
        (await Repository.TryInsertAsync(duplicate, CancellationToken.None)).Should().BeFalse();

        var stored = await Repository.GetAsync(entry.JobId, CancellationToken.None);
        stored.Should().BeEquivalentTo(entry);
    }

    [Fact]
    public async Task Unknown_job_returns_null() =>
        (await Repository.GetAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
}

public sealed class InMemoryAuditRepositoryTests : AuditRepositoryContract
{
    protected override IAuditRepository Repository { get; } = new InMemoryAuditRepository();
}

public sealed class AuditMongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder().WithImage("mongo:7.0").Build();

    public IMongoDatabase CreateDatabase()
    {
        AuditMongoMappings.Register();
        return new MongoClient(_container.GetConnectionString()).GetDatabase($"audit_{Guid.NewGuid():N}");
    }

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[Trait("Category", "Integration")]
public sealed class MongoAuditRepositoryTests(AuditMongoFixture mongo) : AuditRepositoryContract, IClassFixture<AuditMongoFixture>
{
    protected override IAuditRepository Repository { get; } = new MongoAuditRepository(mongo.CreateDatabase());
}
