using Ta9.Contracts.Events;
using Ta9.ProcessorService.Infrastructure.Events;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Infrastructure.Events;

public class EventOutboxTests
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);
    private readonly ManualTimeProvider _time = new();

    internal static ProcessorEventEnvelope Envelope(string type = ProcessorEventTypes.JobStarted, Guid? jobId = null) =>
        ProcessorEventSerializer.Create(type, jobId ?? Guid.NewGuid(), new JobStartedPayload(1), DateTimeOffset.UtcNow, correlationId: null);

    private EventOutbox Connected(int capacity = 10)
    {
        var outbox = new EventOutbox(capacity, _time);
        outbox.MarkConnected();
        return outbox;
    }

    [Fact]
    public void Rejects_lifecycle_events_beyond_capacity()
    {
        var outbox = new EventOutbox(2, _time);

        outbox.TryAddLifecycle(Envelope()).Should().BeTrue();
        outbox.TryAddLifecycle(Envelope()).Should().BeTrue();
        outbox.TryAddLifecycle(Envelope()).Should().BeFalse();
        outbox.PendingCount.Should().Be(2);
    }

    [Fact]
    public void Takes_unsent_events_in_order_only_once()
    {
        var outbox = Connected();
        var first = Envelope();
        var second = Envelope();
        outbox.TryAddLifecycle(first);
        outbox.TryAddLifecycle(second);

        outbox.TakeDue(AckTimeout).Should().Equal(first, second);
        outbox.TakeDue(AckTimeout).Should().BeEmpty("sent events wait for an ack");
        outbox.PendingCount.Should().Be(2);
    }

    [Fact]
    public void Resends_events_whose_ack_timed_out()
    {
        var outbox = Connected();
        var envelope = Envelope();
        outbox.TryAddLifecycle(envelope);
        outbox.TakeDue(AckTimeout);

        _time.Advance(AckTimeout);

        outbox.TakeDue(AckTimeout).Should().Equal(envelope);
    }

    [Fact]
    public void Acknowledge_removes_the_event()
    {
        var outbox = Connected();
        var envelope = Envelope();
        outbox.TryAddLifecycle(envelope);
        outbox.TakeDue(AckTimeout);

        outbox.Acknowledge(envelope.EventId).Should().BeTrue();
        outbox.Acknowledge(envelope.EventId).Should().BeFalse("duplicate acks are harmless");

        outbox.PendingCount.Should().Be(0);
        _time.Advance(AckTimeout);
        outbox.TakeDue(AckTimeout).Should().BeEmpty();
    }

    [Fact]
    public void Reconnect_makes_unacknowledged_events_due_again()
    {
        var outbox = Connected();
        var acked = Envelope();
        var unacked = Envelope();
        outbox.TryAddLifecycle(acked);
        outbox.TryAddLifecycle(unacked);
        outbox.TakeDue(AckTimeout);
        outbox.Acknowledge(acked.EventId);

        outbox.MarkDisconnected();
        outbox.MarkConnected();

        outbox.TakeDue(AckTimeout).Should().Equal(unacked);
    }

    [Fact]
    public void Lifecycle_events_are_buffered_while_disconnected()
    {
        var outbox = new EventOutbox(10, _time);
        var envelope = Envelope();

        outbox.TryAddLifecycle(envelope);
        outbox.MarkConnected();

        outbox.TakeDue(AckTimeout).Should().Equal(envelope);
    }

    [Fact]
    public void Progress_is_dropped_while_disconnected()
    {
        var outbox = new EventOutbox(10, _time);

        outbox.OfferProgress(Envelope(ProcessorEventTypes.JobProgress));
        outbox.MarkConnected();

        outbox.TakeDue(AckTimeout).Should().BeEmpty();
    }

    [Fact]
    public void Progress_is_coalesced_to_the_latest_per_job_and_not_kept_for_ack()
    {
        var outbox = Connected();
        var jobId = Guid.NewGuid();
        var older = Envelope(ProcessorEventTypes.JobProgress, jobId);
        var newer = Envelope(ProcessorEventTypes.JobProgress, jobId);

        outbox.OfferProgress(older);
        outbox.OfferProgress(newer);

        outbox.TakeDue(AckTimeout).Should().Equal(newer);
        outbox.PendingCount.Should().Be(0);
        outbox.TakeDue(AckTimeout).Should().BeEmpty();
    }

    [Fact]
    public void Terminal_event_discards_pending_progress_of_the_job()
    {
        var outbox = Connected();
        var jobId = Guid.NewGuid();
        outbox.OfferProgress(Envelope(ProcessorEventTypes.JobProgress, jobId));
        var completed = Envelope(ProcessorEventTypes.JobCompleted, jobId);

        outbox.TryAddLifecycle(completed);

        outbox.TakeDue(AckTimeout).Should().Equal(completed);
    }

    [Fact]
    public void Disconnect_discards_pending_progress()
    {
        var outbox = Connected();
        outbox.OfferProgress(Envelope(ProcessorEventTypes.JobProgress));

        outbox.MarkDisconnected();
        outbox.MarkConnected();

        outbox.TakeDue(AckTimeout).Should().BeEmpty();
    }

    [Fact]
    public async Task Wait_for_work_completes_when_an_event_is_added()
    {
        var outbox = Connected();
        outbox.TakeDue(AckTimeout);
        await outbox.WaitForWorkAsync(CancellationToken.None); // consume the MarkConnected signal

        var waiting = outbox.WaitForWorkAsync(CancellationToken.None);
        waiting.IsCompleted.Should().BeFalse();

        outbox.TryAddLifecycle(Envelope());

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
