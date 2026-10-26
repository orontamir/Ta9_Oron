using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Tests.TestSupport;
using Ta9.Tests.Shared;

namespace Ta9.JobService.Tests.Application;

/// <summary>What the dispatcher logs: the flow at Information, details at Debug, failures at Error, always with the job id.</summary>
public class JobDispatcherLoggingTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ManualTimeProvider _time = new(TestJobs.T0);
    private readonly InMemoryJobRepository _jobs = new();
    private readonly InMemoryJobDocumentRepository _documents = new();
    private readonly FakeProcessorClient _processor = new();
    private readonly ListLogger<JobDispatcher> _logger = new();

    private JobDispatcher CreateDispatcher() =>
        new(_jobs, _documents, _processor, new JobDispatchSignal(_time),
            Options.Create(new DispatchOptions { MaxDispatchAttempts = 2, InitialBackoff = TimeSpan.FromSeconds(2) }),
            _time, _logger);

    private async Task<Job> PendingJobAsync()
    {
        var job = Job.Create(Guid.NewGuid(), "job", 1, "corr", _time.GetUtcNow());
        await _documents.InsertManyAsync([JobDocument.Create(job.Id, 0, "doc-0", "a b")], None);
        await _jobs.InsertAsync(job, None);
        return job;
    }

    [Fact]
    public async Task Accepted_dispatch_logs_the_flow_at_information_and_details_at_debug()
    {
        var job = await PendingJobAsync();

        await CreateDispatcher().DispatchDueJobsAsync(None);

        _logger.AtLevel(LogLevel.Information).Should().ContainSingle(e => e.Message == $"Job {job.Id} dispatched to the processor");
        _logger.AtLevel(LogLevel.Debug).Select(e => e.Message).Should()
            .Contain(m => m.StartsWith($"Claimed job {job.Id} for dispatch"))
            .And.Contain($"Processor answered Accepted for job {job.Id} (1 documents)");
        _logger.Entries.Where(e => e.Level >= LogLevel.Debug && e.Message.Contains(job.Id.ToString()))
            .Should().OnlyContain(e => Equals(e.Scope.GetValueOrDefault("JobId"), job.Id), "every line of a dispatch carries the job id");
        _logger.AtLevel(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Unreachable_processor_logs_warnings_then_an_error_when_giving_up()
    {
        var job = await PendingJobAsync();
        _processor.OnSubmit = _ => Task.FromResult(DispatchResult.Unreachable("connection refused"));
        var dispatcher = CreateDispatcher();

        await dispatcher.DispatchDueJobsAsync(None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchDueJobsAsync(None);

        _logger.AtLevel(LogLevel.Warning).Should().ContainSingle(e => e.Message.Contains("Processor unreachable (attempt 1)"));
        var error = _logger.AtLevel(LogLevel.Error).Should().ContainSingle().Which;
        error.Message.Should().Contain($"Giving up on job {job.Id}").And.Contain("connection refused");
        error.Scope.GetValueOrDefault("JobId").Should().Be(job.Id);
    }

    [Fact]
    public async Task Rejected_payload_is_logged_as_an_error()
    {
        var job = await PendingJobAsync();
        _processor.OnSubmit = _ => Task.FromResult(DispatchResult.Rejected("400 invalid document"));

        await CreateDispatcher().DispatchDueJobsAsync(None);

        _logger.AtLevel(LogLevel.Error).Should().ContainSingle(e => e.Message == $"Processor rejected job {job.Id}: 400 invalid document");
    }

    [Fact]
    public async Task Unexpected_failure_is_logged_as_an_error_with_the_exception()
    {
        var job = await PendingJobAsync();
        _processor.OnSubmit = _ => throw new InvalidOperationException("serializer exploded");

        await CreateDispatcher().DispatchDueJobsAsync(None);

        var error = _logger.AtLevel(LogLevel.Error).Should().ContainSingle().Which;
        error.Message.Should().Contain($"Dispatch of job {job.Id} failed unexpectedly");
        error.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("serializer exploded");
    }
}
