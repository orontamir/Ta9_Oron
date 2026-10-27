using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ta9.Contracts.Events;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Tests.Application;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Api;

/// <summary>Real WebSocket traffic against the Job Service endpoint (TestServer).</summary>
public sealed class ProcessorEventsEndpointTests : IClassFixture<JobServiceFactory>
{
    private static readonly Uri EndpointUri = new("ws://localhost/ws/processor-events");

    private readonly JobServiceFactory _factory;

    public ProcessorEventsEndpointTests(JobServiceFactory factory) => _factory = factory;

    private static Task<WebSocket> ConnectAsync(JobServiceFactory factory) =>
        factory.Server.CreateWebSocketClient().ConnectAsync(EndpointUri, CancellationToken.None);

    private static Task SendAsync(WebSocket socket, ProcessorEventEnvelope envelope) =>
        SendRawAsync(socket, ProcessorEventSerializer.Serialize(envelope));

    private static Task SendRawAsync(WebSocket socket, byte[] payload) =>
        socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).AsTask();

    /// <summary>Returns the next ack, or null if none arrives within <paramref name="timeout"/>.</summary>
    private static async Task<EventAck?> ReceiveAckAsync(WebSocket socket, TimeSpan? timeout = null)
    {
        var buffer = new byte[4096];
        var receive = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var finished = await Task.WhenAny(receive, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        if (finished != receive)
        {
            return null;
        }

        var result = await receive;
        return ProcessorEventSerializer.TryDeserializeAck(buffer.AsSpan(0, result.Count));
    }

    private async Task<Job> QueuedJobAsync()
    {
        var job = TestJobs.InStatus(JobStatus.Queued);
        await _factory.Jobs.InsertAsync(job, CancellationToken.None);
        return job;
    }

    [Fact]
    public async Task Handled_events_are_acknowledged_and_applied()
    {
        var job = await QueuedJobAsync();
        using var socket = await ConnectAsync(_factory);
        var started = ProcessorEventHandlerTests.Started(job.Id);

        await SendAsync(socket, started);

        (await ReceiveAckAsync(socket))!.EventId.Should().Be(started.EventId);
        (await _factory.Jobs.GetAsync(job.Id, CancellationToken.None))!.Status.Should().Be(JobStatus.Processing);
    }

    [Fact]
    public async Task Duplicates_and_unknown_jobs_are_acknowledged_too()
    {
        var job = await QueuedJobAsync();
        using var socket = await ConnectAsync(_factory);
        var started = ProcessorEventHandlerTests.Started(job.Id);
        var unknown = ProcessorEventHandlerTests.Started(Guid.NewGuid());

        await SendAsync(socket, started);
        (await ReceiveAckAsync(socket))!.EventId.Should().Be(started.EventId);
        await SendAsync(socket, started);
        (await ReceiveAckAsync(socket))!.EventId.Should().Be(started.EventId);
        await SendAsync(socket, unknown);
        (await ReceiveAckAsync(socket))!.EventId.Should().Be(unknown.EventId);
    }

    [Fact]
    public async Task Unreadable_message_is_skipped_and_the_connection_keeps_working()
    {
        var job = await QueuedJobAsync();
        using var socket = await ConnectAsync(_factory);

        await SendRawAsync(socket, Encoding.UTF8.GetBytes("{ definitely not an event"));
        var started = ProcessorEventHandlerTests.Started(job.Id);
        await SendAsync(socket, started);

        (await ReceiveAckAsync(socket))!.EventId.Should().Be(started.EventId, "the bad message got no ack, the next one did");
    }

    [Fact]
    public async Task Events_are_processed_in_order_per_connection()
    {
        var job = await QueuedJobAsync();
        using var socket = await ConnectAsync(_factory);
        var events = new[]
        {
            ProcessorEventHandlerTests.Started(job.Id),
            ProcessorEventHandlerTests.Event(ProcessorEventTypes.JobProgress, job.Id, new JobProgressPayload(2, 3)),
            ProcessorEventHandlerTests.Completed(job.Id)
        };

        foreach (var envelope in events)
        {
            await SendAsync(socket, envelope);
        }

        var acks = new List<Guid>();
        for (var i = 0; i < events.Length; i++)
        {
            acks.Add((await ReceiveAckAsync(socket))!.EventId);
        }

        acks.Should().Equal(events.Select(e => e.EventId));
        (await _factory.Jobs.GetAsync(job.Id, CancellationToken.None))!.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task Plain_http_request_is_rejected()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync("/ws/processor-events")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Event_that_cannot_be_stored_is_not_acknowledged()
    {
        using var failingFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IJobRepository>(new ProcessorEventHandlerTests.ThrowingJobRepository())));
        using var socket = await failingFactory.Server.CreateWebSocketClient().ConnectAsync(EndpointUri, CancellationToken.None);

        await SendAsync(socket, ProcessorEventHandlerTests.Started(Guid.NewGuid()));

        (await ReceiveAckAsync(socket, TimeSpan.FromMilliseconds(500))).Should().BeNull("the processor must keep and resend it");
        socket.State.Should().Be(WebSocketState.Open);
    }
}
