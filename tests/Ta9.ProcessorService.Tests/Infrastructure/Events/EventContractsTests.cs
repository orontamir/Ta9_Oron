using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Ta9.Contracts.Events;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Infrastructure.Events;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Infrastructure.Events;

public class ProcessorEventSerializerTests
{
    private static readonly DocumentResultDto Result = new(
        0, "a.txt", DocumentStatus.Succeeded, 3, 2, [new WordCountDto("hello", 2)], "abc", 17, 1, 5.0, "hello", null);

    [Fact]
    public void Envelope_round_trips_with_typed_payload()
    {
        var jobId = Guid.NewGuid();
        var payload = new JobCompletedPayload(new ProcessingSummaryDto(1, 1, 0, 3, 12), [Result]);
        var envelope = ProcessorEventSerializer.Create(ProcessorEventTypes.JobCompleted, jobId, payload, DateTimeOffset.UtcNow, "corr-1");

        var parsed = ProcessorEventSerializer.Deserialize(ProcessorEventSerializer.Serialize(envelope));

        parsed.EventId.Should().Be(envelope.EventId);
        parsed.Type.Should().Be(ProcessorEventTypes.JobCompleted);
        parsed.JobId.Should().Be(jobId);
        parsed.CorrelationId.Should().Be("corr-1");
        parsed.SchemaVersion.Should().Be(ProcessorEventSerializer.CurrentSchemaVersion);
        var parsedPayload = ProcessorEventSerializer.GetPayload<JobCompletedPayload>(parsed);
        parsedPayload.Summary.Should().Be(payload.Summary);
        parsedPayload.Results.Should().ContainSingle().Which.Should().BeEquivalentTo(Result);
    }

    [Fact]
    public void Uses_camel_case_and_string_enums_on_the_wire()
    {
        var envelope = ProcessorEventSerializer.Create(
            ProcessorEventTypes.JobCompleted, Guid.NewGuid(),
            new JobCompletedPayload(new ProcessingSummaryDto(1, 1, 0, 3, 12), [Result]),
            DateTimeOffset.UtcNow, null);

        var json = Encoding.UTF8.GetString(ProcessorEventSerializer.Serialize(envelope));

        json.Should().Contain("\"eventId\"").And.Contain("\"type\":\"JobCompleted\"").And.Contain("\"status\":\"Succeeded\"");
    }

    [Fact]
    public void Ack_round_trips()
    {
        var eventId = Guid.NewGuid();

        var ack = ProcessorEventSerializer.TryDeserializeAck(ProcessorEventSerializer.SerializeAck(eventId));

        ack.Should().Be(EventAck.For(eventId));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"Other\",\"eventId\":\"6f1c2a3e-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"type\":\"Ack\"}")]
    public void Non_ack_messages_are_ignored(string message) =>
        ProcessorEventSerializer.TryDeserializeAck(Encoding.UTF8.GetBytes(message)).Should().BeNull();
}

public class WebSocketJobEventPublisherTests
{
    private readonly EventOutbox _outbox = new(10, TimeProvider.System);
    private readonly WebSocketJobEventPublisher _publisher;

    public WebSocketJobEventPublisherTests() =>
        _publisher = new WebSocketJobEventPublisher(_outbox, TimeProvider.System, NullLogger<WebSocketJobEventPublisher>.Instance);

    private static ProcessingJob Job() =>
        new(Guid.NewGuid(), TestData.Documents("a b", "c"), correlationId: "corr-9", DateTimeOffset.UtcNow);

    [Fact]
    public void Lifecycle_events_are_queued_with_job_metadata()
    {
        var job = Job();

        _publisher.JobStarted(job);

        var envelope = _outbox.TakeDue(TimeSpan.FromMinutes(1)).Should().ContainSingle().Which;
        envelope.Type.Should().Be(ProcessorEventTypes.JobStarted);
        envelope.JobId.Should().Be(job.JobId);
        envelope.CorrelationId.Should().Be("corr-9");
        ProcessorEventSerializer.GetPayload<JobStartedPayload>(envelope).TotalDocuments.Should().Be(2);
    }

    [Fact]
    public void Completed_event_carries_summary_and_results()
    {
        var job = Job();
        job.TryStart(DateTimeOffset.UtcNow);
        var results = new List<DocumentResultDto>();
        job.TryComplete(results, new ProcessingSummaryDto(2, 2, 0, 3, 5), DateTimeOffset.UtcNow);

        _publisher.JobCompleted(job);

        var envelope = _outbox.TakeDue(TimeSpan.FromMinutes(1)).Single();
        ProcessorEventSerializer.GetPayload<JobCompletedPayload>(envelope).Summary.TotalWords.Should().Be(3);
    }

    [Fact]
    public void Progress_is_dropped_while_disconnected()
    {
        _publisher.JobProgress(Job());

        _outbox.MarkConnected();
        _outbox.TakeDue(TimeSpan.FromMinutes(1)).Should().BeEmpty();
    }

    [Fact]
    public void Full_outbox_does_not_throw()
    {
        var outbox = new EventOutbox(1, TimeProvider.System);
        var publisher = new WebSocketJobEventPublisher(outbox, TimeProvider.System, NullLogger<WebSocketJobEventPublisher>.Instance);
        var job = Job();

        publisher.JobStarted(job);
        FluentActions.Invoking(() => publisher.JobCancelled(job)).Should().NotThrow();
        outbox.PendingCount.Should().Be(1);
    }
}
