using Microsoft.Extensions.Options;

namespace Ta9.ProcessorService.Application;

/// <summary>Evicts finished jobs after <see cref="ProcessingOptions.ResultsTtl"/> so memory stays bounded.</summary>
public sealed class ProcessingJobCleanupService(
    IProcessingJobStore store,
    IOptions<ProcessingOptions> options,
    TimeProvider time,
    ILogger<ProcessingJobCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var removed = store.RemoveFinishedBefore(time.GetUtcNow() - options.Value.ResultsTtl);
                if (removed > 0)
                {
                    logger.LogInformation("Evicted {Count} finished jobs", removed);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
