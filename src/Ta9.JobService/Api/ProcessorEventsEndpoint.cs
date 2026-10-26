using System.Net.WebSockets;
using System.Text.Json;
using Ta9.Contracts.Events;
using Ta9.JobService.Application;

namespace Ta9.JobService.Api;

/// <summary>
/// WebSocket endpoint the Processor connects to. Events of one connection are handled strictly in order
/// (one at a time), and each is acknowledged only after it has been handled, so the Processor's outbox
/// keeps and resends anything this service could not persist.
/// </summary>
public sealed class ProcessorEventsEndpoint(
    ProcessorEventHandler handler,
    IHostApplicationLifetime lifetime,
    ILogger<ProcessorEventsEndpoint> logger)
{
    public const string Path = "/ws/processor-events";
    public const string ProcessorIdHeader = "X-Processor-Id";

    /// <summary>A JobCompleted for 1,000 documents is ~0.5 MB; anything far above that is not a legitimate event.</summary>
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection expected.");
            return;
        }

        var processorId = context.Request.Headers[ProcessorIdHeader].ToString() is { Length: > 0 } id ? id : "unknown";
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        logger.LogInformation("Processor {ProcessorId} connected", processorId);

        try
        {
            await ReceiveLoopAsync(socket, stop.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Connection dropped or shutting down; the Processor reconnects and replays unacknowledged events.
        }
        finally
        {
            logger.LogInformation("Processor {ProcessorId} disconnected", processorId);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    logger.LogError("Closing processor connection: message exceeds {MaxBytes} bytes", MaxMessageBytes);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None);
                    return;
                }
            }
            while (!result.EndOfMessage);

            await HandleMessageAsync(socket, message.GetBuffer().AsMemory(0, (int)message.Length), cancellationToken);
        }
    }

    private async Task HandleMessageAsync(WebSocket socket, ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
    {
        ProcessorEventEnvelope envelope;
        try
        {
            envelope = ProcessorEventSerializer.Deserialize(utf8Json.Span);
        }
        catch (JsonException ex)
        {
            // Without a parsable eventId there is nothing to acknowledge.
            logger.LogWarning("Ignoring unreadable message from processor: {Error}", ex.Message);
            return;
        }

        try
        {
            await handler.HandleAsync(envelope, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Typically storage unavailable: do not ack, the Processor resends after its ack timeout.
            logger.LogError(ex, "Failed to handle {Type} {EventId} for job {JobId}; not acknowledging", envelope.Type, envelope.EventId, envelope.JobId);
            return;
        }

        await socket.SendAsync(ProcessorEventSerializer.SerializeAck(envelope.EventId).AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        logger.LogDebug("Acknowledged {Type} {EventId}", envelope.Type, envelope.EventId);
    }
}
