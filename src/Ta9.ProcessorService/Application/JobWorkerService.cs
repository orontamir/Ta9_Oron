using Microsoft.Extensions.Options;

namespace Ta9.ProcessorService.Application;

/// <summary>
/// Runs <see cref="ProcessingOptions.MaxConcurrentJobs"/> long-lived worker loops that pull jobs from the
/// bounded queue. Idle workers await the channel, so no thread is blocked while waiting for work.
/// </summary>
public sealed class JobWorkerService(
    IJobQueue queue,
    JobRunner runner,
    IOptions<ProcessingOptions> options,
    ILogger<JobWorkerService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = options.Value.MaxConcurrentJobs;
        logger.LogInformation(
            "Starting {WorkerCount} job workers (document parallelism per job: {DocParallelism})",
            workerCount, options.Value.DocParallelismPerJob);

        var workers = Enumerable.Range(0, workerCount).Select(id => RunWorkerAsync(id, stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await runner.RunAsync(job, stoppingToken);
                }
                catch (Exception ex)
                {
                    // A worker must survive any single job.
                    logger.LogError(ex, "Worker {WorkerId} crashed on job {JobId}", workerId, job.JobId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
