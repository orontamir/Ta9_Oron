using System.Net.WebSockets;
using Microsoft.Extensions.Options;

namespace Ta9.ProcessorService.Infrastructure.Events;

/// <summary>Opens the WebSocket. Abstracted so tests can connect to an in-memory TestServer.</summary>
public interface IWebSocketConnector
{
    Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class ClientWebSocketConnector(IOptions<JobServiceEventsOptions> options) : IWebSocketConnector
{
    public const string ProcessorIdHeader = "X-Processor-Id";

    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = options.Value.KeepAliveInterval;
        socket.Options.SetRequestHeader(ProcessorIdHeader, Environment.MachineName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.ConnectTimeout);
        try
        {
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
