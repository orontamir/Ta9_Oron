using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Ta9.Contracts.Events;
using Ta9.ProcessorService.Infrastructure.Events;

namespace Ta9.ProcessorService.Tests.TestSupport;

/// <summary>
/// In-memory stand-in for the Job Service WS endpoint: records every event it receives and
/// (optionally) acknowledges it. Runs on TestServer, so the WebSocket traffic is real but no port is opened.
/// </summary>
public sealed class FakeJobServiceServer : IAsyncDisposable
{
    public static readonly Uri Url = new("ws://localhost/ws/processor-events");

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ProcessorEventEnvelope> _received = new();
    private WebSocket? _current;
    private int _connections;

    private FakeJobServiceServer(WebApplication app) => _app = app;

    public bool AutoAck { get; set; } = true;
    public int Connections => Volatile.Read(ref _connections);
    public IReadOnlyList<ProcessorEventEnvelope> Received => _received.ToList();
    public TestServer Server => _app.GetTestServer();

    public static async Task<FakeJobServiceServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var server = new FakeJobServiceServer(app);
        app.UseWebSockets();
        app.Map(Url.AbsolutePath, (RequestDelegate)server.HandleAsync);
        await app.StartAsync();
        return server;
    }

    public TestServerConnector Connector(int failFirstAttempts = 0) => new(Server, failFirstAttempts);

    /// <summary>Server-initiated close of the current connection (simulates a Job Service restart/network drop).</summary>
    public async Task DropConnectionAsync()
    {
        var socket = _current;
        if (socket is { State: WebSocketState.Open })
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test drop", CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Interlocked.Increment(ref _connections);
        _current = socket;

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (socket.State == WebSocketState.CloseReceived)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        }
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var envelope = ProcessorEventSerializer.Deserialize(message.ToArray());
                _received.Enqueue(envelope);
                if (AutoAck)
                {
                    await socket.SendAsync(
                        ProcessorEventSerializer.SerializeAck(envelope.EventId).AsMemory(),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        context.RequestAborted);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            // Connection dropped; the client will reconnect.
        }
    }
}

/// <summary>Connects to a TestServer; can simulate an unreachable Job Service for the first N attempts.</summary>
public sealed class TestServerConnector(TestServer server, int failFirstAttempts = 0) : IWebSocketConnector
{
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _attempts) <= failFirstAttempts)
        {
            throw new WebSocketException("Simulated: Job Service unreachable");
        }

        return await server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);
    }
}
