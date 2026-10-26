using System.Threading.Channels;
using Ta9.Contracts.Events;

namespace Ta9.ProcessorService.Infrastructure.Events;

/// <summary>
/// Delivery state of events towards the Job Service.
/// <list type="bullet">
/// <item>Lifecycle events (started/completed/failed/cancelled) are kept, in order, until acknowledged
/// (at-least-once). They are resent after a reconnect or when no ack arrives within the ack timeout.</item>
/// <item>Progress events are best-effort: coalesced to the latest per job, dropped while disconnected
/// and never resent.</item>
/// </list>
/// Thread-safe; bounded by <c>capacity</c> lifecycle events plus at most one progress event per job.
/// </summary>
public sealed class EventOutbox(int capacity, TimeProvider time)
{
    private sealed class Entry(ProcessorEventEnvelope envelope)
    {
        public ProcessorEventEnvelope Envelope { get; } = envelope;
        public DateTimeOffset? SentAt { get; set; }
    }

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _lifecycle = new();
    private readonly Dictionary<Guid, LinkedListNode<Entry>> _byEventId = new();
    private readonly Dictionary<Guid, ProcessorEventEnvelope> _progressByJob = new();
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private bool _connected;

    public bool IsConnected { get { lock (_gate) return _connected; } }

    /// <summary>Lifecycle events waiting to be sent or acknowledged.</summary>
    public int PendingCount { get { lock (_gate) return _lifecycle.Count; } }

    public bool TryAddLifecycle(ProcessorEventEnvelope envelope)
    {
        lock (_gate)
        {
            if (_lifecycle.Count >= capacity)
            {
                return false;
            }

            _byEventId[envelope.EventId] = _lifecycle.AddLast(new Entry(envelope));
            if (ProcessorEventTypes.IsTerminal(envelope.Type))
            {
                // Progress is meaningless once the job reached a terminal state.
                _progressByJob.Remove(envelope.JobId);
            }
        }

        Signal();
        return true;
    }

    public void OfferProgress(ProcessorEventEnvelope envelope)
    {
        lock (_gate)
        {
            if (!_connected)
            {
                return;
            }

            _progressByJob[envelope.JobId] = envelope;
        }

        Signal();
    }

    /// <summary>
    /// Returns what should go on the wire now: lifecycle events never sent (or whose ack timed out), in order,
    /// followed by the latest progress per job. Lifecycle events are marked as sent; progress is consumed.
    /// </summary>
    public IReadOnlyList<ProcessorEventEnvelope> TakeDue(TimeSpan ackTimeout)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var due = new List<ProcessorEventEnvelope>();
            foreach (var entry in _lifecycle)
            {
                if (entry.SentAt is null || now - entry.SentAt.Value >= ackTimeout)
                {
                    entry.SentAt = now;
                    due.Add(entry.Envelope);
                }
            }

            due.AddRange(_progressByJob.Values);
            _progressByJob.Clear();
            return due;
        }
    }

    public bool Acknowledge(Guid eventId)
    {
        lock (_gate)
        {
            if (!_byEventId.Remove(eventId, out var node))
            {
                return false; // progress events and duplicate acks land here
            }

            _lifecycle.Remove(node);
            return true;
        }
    }

    /// <summary>A new connection is up: everything not acknowledged must be sent again.</summary>
    public void MarkConnected()
    {
        lock (_gate)
        {
            _connected = true;
            foreach (var entry in _lifecycle)
            {
                entry.SentAt = null;
            }
        }

        Signal();
    }

    public void MarkDisconnected()
    {
        lock (_gate)
        {
            _connected = false;
            _progressByJob.Clear();
        }
    }

    /// <summary>Completes when new work was added since the last wait (or immediately if already signalled).</summary>
    public async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        await _signal.Reader.WaitToReadAsync(cancellationToken);
        _signal.Reader.TryRead(out _);
    }

    private void Signal() => _signal.Writer.TryWrite(0);
}
