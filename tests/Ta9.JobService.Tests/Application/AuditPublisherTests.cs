using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Audit;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Audit;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Application;

public class AuditPublisherTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class FakeAuditClient : IAuditClient
    {
        private readonly ConcurrentQueue<JobAuditRequest> _received = new();

        public Func<JobAuditRequest, AuditPublishOutcome> Respond { get; set; } = _ => AuditPublishOutcome.Recorded;
        public IReadOnlyList<JobAuditRequest> Received => _received.ToList();

        public Task<AuditPublishOutcome> PublishAsync(JobAuditRequest request, CancellationToken cancellationToken)
        {
            _received.Enqueue(request);
            return Task.FromResult(Respond(request));
        }
    }

    private readonly InMemoryJobRepository _jobs = new();
    private readonly FakeAuditClient _audit = new();
    private readonly AuditPublisher _publisher;

    public AuditPublisherTests() =>
        _publisher = new AuditPublisher(_jobs, _audit, Options.Create(new AuditOptions()), TimeProvider.System, NullLogger<AuditPublisher>.Instance);

    private async Task<Job> InsertAsync(JobStatus status)
    {
        var job = TestJobs.InStatus(status);
        await _jobs.InsertAsync(job, None);
        return job;
    }

    [Fact]
    public async Task Publishes_finished_jobs_once_and_marks_them()
    {
        var completed = await InsertAsync(JobStatus.Completed);
        var cancelled = await InsertAsync(JobStatus.Cancelled);
        await InsertAsync(JobStatus.Processing);

        var report = await _publisher.PublishPendingAsync(None);

        report.Published.Should().Be(2);
        _audit.Received.Select(r => r.JobId).Should().BeEquivalentTo(new[] { completed.Id, cancelled.Id });
        (await _jobs.GetAsync(completed.Id, None))!.AuditPublished.Should().BeTrue();

        (await _publisher.PublishPendingAsync(None)).Published.Should().Be(0);
        _audit.Received.Should().HaveCount(2);
    }

    [Fact]
    public async Task Summary_carries_the_job_outcome()
    {
        var job = await InsertAsync(JobStatus.Completed);

        await _publisher.PublishPendingAsync(None);

        var request = _audit.Received.Single();
        request.Status.Should().Be(AuditJobStatuses.Completed);
        request.TotalDocuments.Should().Be(job.TotalDocuments);
        request.SucceededDocuments.Should().Be(TestJobs.Summary().Succeeded);
        request.FailedDocuments.Should().Be(TestJobs.Summary().Failed);
        request.TotalWords.Should().Be(TestJobs.Summary().TotalWords);
        request.CompletedAt.Should().Be(job.CompletedAt!.Value);
    }

    [Fact]
    public async Task Failed_job_summary_includes_the_reason()
    {
        await InsertAsync(JobStatus.Failed);

        await _publisher.PublishPendingAsync(None);

        var request = _audit.Received.Single();
        request.Status.Should().Be(AuditJobStatuses.Failed);
        request.FailureReason.Should().Be("boom");
    }

    [Fact]
    public async Task Unavailable_audit_service_keeps_jobs_pending_and_stops_the_batch()
    {
        var first = await InsertAsync(JobStatus.Completed);
        await InsertAsync(JobStatus.Completed);
        _audit.Respond = _ => AuditPublishOutcome.Unavailable;

        var report = await _publisher.PublishPendingAsync(None);

        report.AuditUnavailable.Should().BeTrue();
        _audit.Received.Should().HaveCount(1, "no point hammering a service that is down");
        (await _jobs.GetAsync(first.Id, None))!.AuditPublished.Should().BeFalse();

        _audit.Respond = _ => AuditPublishOutcome.Recorded;
        (await _publisher.PublishPendingAsync(None)).Published.Should().Be(2);
    }

    [Fact]
    public async Task Rejected_summary_is_not_retried_forever()
    {
        var job = await InsertAsync(JobStatus.Completed);
        _audit.Respond = _ => AuditPublishOutcome.Rejected;

        var report = await _publisher.PublishPendingAsync(None);

        report.Rejected.Should().Be(1);
        (await _jobs.GetAsync(job.Id, None))!.AuditPublished.Should().BeTrue();
    }

    [Fact]
    public async Task Job_state_is_untouched_by_publishing()
    {
        var job = await InsertAsync(JobStatus.Completed);

        await _publisher.PublishPendingAsync(None);

        var reloaded = (await _jobs.GetAsync(job.Id, None))!;
        reloaded.Status.Should().Be(JobStatus.Completed);
        reloaded.UpdatedAt.Should().Be(job.UpdatedAt);
    }
}

public class HttpAuditClientTests
{
    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(respond)) { BaseAddress = new Uri("http://audit/") };

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }
    }

    private static readonly JobAuditRequest Request = new(
        Guid.NewGuid(), "job", AuditJobStatuses.Completed, 3, 3, 0, 10, null, TestJobs.T0, TestJobs.T0.AddSeconds(1));

    private static Task<AuditPublishOutcome> PublishAsync(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new HttpAuditClient(new StubFactory(respond), NullLogger<HttpAuditClient>.Instance).PublishAsync(Request, CancellationToken.None);

    [Theory]
    [InlineData(HttpStatusCode.Created, AuditPublishOutcome.Recorded)]
    [InlineData(HttpStatusCode.OK, AuditPublishOutcome.Recorded)]
    [InlineData(HttpStatusCode.BadRequest, AuditPublishOutcome.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, AuditPublishOutcome.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AuditPublishOutcome.Unavailable)]
    public async Task Maps_status_codes(HttpStatusCode status, AuditPublishOutcome expected) =>
        (await PublishAsync(_ => new HttpResponseMessage(status))).Should().Be(expected);

    [Fact]
    public async Task Posts_to_the_audit_endpoint()
    {
        string? path = null;

        await PublishAsync(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        path.Should().Be("/api/audit");
    }

    [Fact]
    public async Task Connection_failure_is_unavailable() =>
        (await PublishAsync(_ => throw new HttpRequestException("refused"))).Should().Be(AuditPublishOutcome.Unavailable);
}
