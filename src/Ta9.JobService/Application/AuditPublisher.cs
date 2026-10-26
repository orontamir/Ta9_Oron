using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Audit;
using Ta9.JobService.Domain;
using Ta9.ServiceDefaults;

namespace Ta9.JobService.Application;

public enum AuditPublishOutcome
{
    /// <summary>201 or 200 (already recorded; the audit service is idempotent).</summary>
    Recorded,

    /// <summary>4xx: our payload is invalid; retrying cannot help.</summary>
    Rejected,

    /// <summary>Network error, timeout or 5xx: retry later.</summary>
    Unavailable
}

public interface IAuditClient
{
    Task<AuditPublishOutcome> PublishAsync(JobAuditRequest request, CancellationToken cancellationToken);
}

public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    public bool Enabled { get; set; } = true;

    [Required]
    public Uri BaseUrl { get; set; } = new("http://localhost:5003/");

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;
}

public sealed record AuditPublishReport(int Published, int Rejected, bool AuditUnavailable);

/// <summary>
/// Delivers terminal job summaries to the Audit Service. The job's <see cref="Job.AuditPublished"/> flag is the
/// outbox: nothing is lost if the Audit Service (or this service) is down, delivery resumes where it stopped.
/// Audit problems never affect jobs.
/// </summary>
public sealed class AuditPublisher(
    IJobRepository jobs,
    IAuditClient audit,
    IOptions<AuditOptions> options,
    TimeProvider time,
    ILogger<AuditPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogWarning("Audit publishing is disabled by configuration");
            return;
        }

        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = settings.Interval;
            try
            {
                var report = await PublishPendingAsync(stoppingToken);
                if (report.AuditUnavailable)
                {
                    consecutiveFailures++;
                    delay = ExponentialBackoff.Compute(consecutiveFailures, settings.Interval, settings.MaxBackoff);
                    logger.LogWarning("Audit service unavailable; retrying in {Delay}", delay);
                }
                else
                {
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit publishing pass failed");
            }

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

    public async Task<AuditPublishReport> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await jobs.FindUnpublishedTerminalAsync(options.Value.BatchSize, cancellationToken);
        int published = 0, rejected = 0;

        foreach (var job in pending)
        {
            var outcome = await audit.PublishAsync(ToRequest(job), cancellationToken);
            if (outcome == AuditPublishOutcome.Unavailable)
            {
                // Stop the batch: the next jobs would fail the same way.
                return new AuditPublishReport(published, rejected, AuditUnavailable: true);
            }

            if (outcome == AuditPublishOutcome.Rejected)
            {
                logger.LogError("Audit service rejected the summary of job {JobId}; it will not be retried", job.Id);
                rejected++;
            }
            else
            {
                published++;
            }

            await jobs.UpdateAsync(job.Id, j => j.TryMarkAuditPublished(), cancellationToken);
        }

        if (published > 0)
        {
            logger.LogInformation("Published {Count} job summaries to the audit service", published);
        }

        return new AuditPublishReport(published, rejected, AuditUnavailable: false);
    }

    public static JobAuditRequest ToRequest(Job job) => new(
        job.Id,
        job.Name,
        job.Status.ToString(),
        job.TotalDocuments,
        job.Summary?.Succeeded ?? 0,
        job.Summary?.Failed ?? 0,
        job.Summary?.TotalWords ?? 0,
        job.FailureReason,
        job.CreatedAt,
        job.CompletedAt ?? job.UpdatedAt);
}
