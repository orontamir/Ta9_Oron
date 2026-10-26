using System.Text.Json;
using System.Text.Json.Serialization;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Api;

/// <summary>
/// Server-Sent Events stream of one job: a snapshot first, then every change, then the stream ends when the job
/// reaches a terminal state. SSE fits better than WebSocket here: one-way, plain HTTP, auto-reconnect in browsers.
/// </summary>
public sealed class JobEventStream(IJobUpdateBroadcaster broadcaster, IJobRepository jobs, TimeProvider time)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Streams until the job finishes or the client disconnects. Returns false (nothing written) if the job does not exist.</summary>
    public async Task<bool> StreamAsync(Guid jobId, HttpResponse response, CancellationToken cancellationToken)
    {
        // Subscribe before reading the snapshot so no change can fall between the two.
        using var subscription = broadcaster.Subscribe(jobId);
        var job = await jobs.GetAsync(jobId, cancellationToken);
        if (job is null)
        {
            return false;
        }

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering (nginx)

        var lastVersion = job.Version;
        await WriteEventAsync(response, job, cancellationToken);
        if (job.IsTerminal)
        {
            return true;
        }

        var reader = subscription.Reader;
        try
        {
            while (true)
            {
                bool hasMore;
                try
                {
                    hasMore = await reader.WaitToReadAsync(cancellationToken).AsTask().WaitAsync(HeartbeatInterval, time, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // Comment line: keeps idle connections alive through proxies and load balancers.
                    await response.WriteAsync(": keep-alive\n\n", cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                    continue;
                }

                if (!hasMore)
                {
                    return true;
                }

                while (reader.TryRead(out var update))
                {
                    if (update.Version <= lastVersion)
                    {
                        continue; // already covered by the snapshot or a newer update
                    }

                    lastVersion = update.Version;
                    await WriteEventAsync(response, update, cancellationToken);
                    if (update.IsTerminal)
                    {
                        return true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true; // client went away
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, Job job, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(JobResponse.From(job), Json);
        await response.WriteAsync($"id: {job.Version}\nevent: status\ndata: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
