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
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Storage:Provider", "InMemory");
}

public sealed class AuditApiTests(AuditApiFactory factory) : IClassFixture<AuditApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

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
