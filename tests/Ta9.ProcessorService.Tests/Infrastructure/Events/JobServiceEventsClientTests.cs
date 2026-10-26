using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Events;
using Ta9.ProcessorService.Infrastructure.Events;
using Ta9.ProcessorService.Tests.TestSupport;

namespace Ta9.ProcessorService.Tests.Infrastructure.Events;

/// <summary>Real WebSocket traffic against an in-memory fake Job Service.</summary>
public sealed class JobServiceEventsClientTests : IAsyncLifetime
{
    private readonly EventOutbox _outbox = new(100, TimeProvider.System);
    private FakeJobServiceServer _server = null!;
    private JobServiceEventsClient? _client;

    public async Task InitializeAsync() => _server = await FakeJobServiceServer.StartAsync();

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.StopAsync(CancellationToken.None);
            _client.Dispose();
        }
        await _server.DisposeAsync();
    }

    private async Task StartClientAsync(TestServerConnector connector, TimeSpan? ackTimeout = null)
    {
        var options = Options.Create(new JobServiceEventsOptions
        {
            Url = FakeJobServiceServer.Url,
            AckTimeout = ackTimeout ?? TimeSpan.FromMinutes(5),
            ResendCheckInterval = TimeSpan.FromMilliseconds(50),
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(100)
        });
        _client = new JobServiceEventsClient(_outbox, connector, options, TimeProvider.System, NullLogger<JobServiceEventsClient>.Instance);
        await _client.StartAsync(CancellationToken.None);
    }

    private int TimesReceived(ProcessorEventEnvelope envelope) =>
        _server.Received.Count(e => e.EventId == envelope.EventId);

    [Fact]
    public async Task Delivers_events_in_order_and_clears_outbox_on_ack()
    {
        var events = Enumerable.Range(0, 5).Select(_ => EventOutboxTests.Envelope()).ToList();
        await StartClientAsync(_server.Connector());

        events.ForEach(e => _outbox.TryAddLifecycle(e));

        await Eventually.TrueAsync(() => _server.Received.Count == 5 && _outbox.PendingCount == 0);
        _server.Received.Select(e => e.EventId).Should().Equal(events.Select(e => e.EventId));
        _outbox.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Buffers_events_until_the_job_service_becomes_reachable()
    {
        var envelope = EventOutboxTests.Envelope();
        _outbox.TryAddLifecycle(envelope);
        var connector = _server.Connector(failFirstAttempts: 3);

        await StartClientAsync(connector);

        await Eventually.TrueAsync(() => _outbox.PendingCount == 0);
        connector.Attempts.Should().BeGreaterThanOrEqualTo(4);
        TimesReceived(envelope).Should().Be(1);
    }

    [Fact]
    public async Task Replays_unacknowledged_events_after_reconnect()
    {
        _server.AutoAck = false;
        var envelope = EventOutboxTests.Envelope();
        await StartClientAsync(_server.Connector());
        _outbox.TryAddLifecycle(envelope);
        await Eventually.TrueAsync(() => TimesReceived(envelope) == 1);

        await _server.DropConnectionAsync();

        await Eventually.TrueAsync(() => _server.Connections == 2 && TimesReceived(envelope) == 2);
        _outbox.PendingCount.Should().Be(1, "still not acknowledged");

        _server.AutoAck = true;
        await _server.DropConnectionAsync();
        await Eventually.TrueAsync(() => _outbox.PendingCount == 0);
    }

    [Fact]
    public async Task Resends_when_ack_times_out_without_reconnect()
    {
        _server.AutoAck = false;
        var envelope = EventOutboxTests.Envelope();
        await StartClientAsync(_server.Connector(), ackTimeout: TimeSpan.FromMilliseconds(200));

        _outbox.TryAddLifecycle(envelope);

        await Eventually.TrueAsync(() => TimesReceived(envelope) >= 2);
        _server.Connections.Should().Be(1);
    }

    [Fact]
    public async Task Sends_progress_while_connected_without_waiting_for_ack()
    {
        await StartClientAsync(_server.Connector());
        await Eventually.TrueAsync(() => _outbox.IsConnected);

        _outbox.OfferProgress(EventOutboxTests.Envelope(ProcessorEventTypes.JobProgress));

        await Eventually.TrueAsync(() => _server.Received.Any(e => e.Type == ProcessorEventTypes.JobProgress));
        _outbox.PendingCount.Should().Be(0);
    }
}
