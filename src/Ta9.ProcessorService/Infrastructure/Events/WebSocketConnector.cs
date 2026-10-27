using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Ta9.ServiceDefaults.Security;

namespace Ta9.ProcessorService.Infrastructure.Events;

/// <summary>Opens the WebSocket. Abstracted so tests can connect to an in-memory TestServer.</summary>
public interface IWebSocketConnector
{
    Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class ClientWebSocketConnector(IOptions<JobServiceEventsOptions> options, IOptions<ApiKeyOptions> keys) : IWebSocketConnector
{
    public const string ProcessorIdHeader = "X-Processor-Id";

    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = options.Value.KeepAliveInterval;
        socket.Options.SetRequestHeader(ProcessorIdHeader, Environment.MachineName);
        if (!string.IsNullOrEmpty(keys.Value.ProcessorServiceKey))
        {
            socket.Options.SetRequestHeader(ApiKeyAuthenticationHandler.HeaderName, keys.Value.ProcessorServiceKey);
        }

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
