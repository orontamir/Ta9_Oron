using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Events;
using Ta9.ServiceDefaults;

namespace Ta9.ProcessorService.Infrastructure.Events;

/// <summary>
/// Keeps one WebSocket connection to the Job Service alive: connects with exponential backoff, replays
/// unacknowledged events from the <see cref="EventOutbox"/> after every (re)connect, sends new events
/// as they arrive and removes them from the outbox when the Job Service acknowledges them.
/// </summary>
public sealed class JobServiceEventsClient(
    EventOutbox outbox,
    IWebSocketConnector connector,
    IOptions<JobServiceEventsOptions> options,
    TimeProvider time,
    ILogger<JobServiceEventsClient> logger) : BackgroundService
{
    private const int MaxAckMessageBytes = 64 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var failedAttempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            WebSocket? socket = null;
            try
            {
                socket = await connector.ConnectAsync(settings.Url, stoppingToken);
                failedAttempts = 0;
                outbox.MarkConnected();
                logger.LogInformation("Connected to Job Service events endpoint {Url}; {Pending} events pending", settings.Url, outbox.PendingCount);

                await RunConnectionAsync(socket, stoppingToken);
                logger.LogWarning("Job Service closed the events connection");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failedAttempts++;
                logger.LogWarning("Job Service events connection failed (attempt {Attempt}): {Error}", failedAttempts, ex.Message);
            }
            finally
            {
                outbox.MarkDisconnected();
                if (socket is not null)
                {
                    await CloseQuietlyAsync(socket);
                    socket.Dispose();
                }
            }

            var delay = ExponentialBackoff.Compute(Math.Max(failedAttempts, 1), settings.ReconnectInitialDelay, settings.ReconnectMaxDelay);
            try
            {
                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunConnectionAsync(WebSocket socket, CancellationToken stoppingToken)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var receiving = ReceiveAcksAsync(socket, connection.Token);
        var sending = SendLoopAsync(socket, connection.Token);

        // Whichever side stops first (server closed, network error, shutdown) ends the connection.
        var first = await Task.WhenAny(receiving, sending);
        await connection.CancelAsync();
        await CloseQuietlyAsync(socket);
        socket.Abort(); // guarantees a pending ReceiveAsync returns, whatever the WebSocket implementation
        await Task.WhenAll(
            receiving.ContinueWith(static _ => { }, TaskScheduler.Default),
            sending.ContinueWith(static _ => { }, TaskScheduler.Default));
        await first; // surface the original failure, if any
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var envelope in outbox.TakeDue(settings.AckTimeout))
            {
                await socket.SendAsync(ProcessorEventSerializer.Serialize(envelope).AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                logger.LogDebug("Sent {Type} {EventId} for job {JobId}", envelope.Type, envelope.EventId, envelope.JobId);
            }

            try
            {
                await outbox.WaitForWorkAsync(cancellationToken).WaitAsync(settings.ResendCheckInterval, time, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Periodic wake-up to resend events whose ack timed out.
            }
        }
    }

    private async Task ReceiveAcksAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxAckMessageBytes)
                {
                    throw new InvalidOperationException("Ack message too large.");
                }
            }
            while (!result.EndOfMessage);

            var ack = ProcessorEventSerializer.TryDeserializeAck(message.GetBuffer().AsSpan(0, (int)message.Length));
            if (ack is null)
            {
                logger.LogWarning("Ignoring unexpected message from Job Service");
                continue;
            }

            outbox.Acknowledge(ack.EventId);
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", timeout.Token);
        }
        catch (Exception)
        {
            // Best effort: the connection is being discarded anyway.
        }
    }
}
