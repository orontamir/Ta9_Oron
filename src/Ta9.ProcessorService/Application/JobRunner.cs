using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Domain;
using Ta9.ServiceDefaults;

namespace Ta9.ProcessorService.Application;

/// <summary>
/// Processes one job: analyzes its documents with bounded parallelism, isolates per-document failures
/// and reports the lifecycle through <see cref="IJobEventPublisher"/>.
/// </summary>
public sealed class JobRunner(
    IDocumentAnalyzer analyzer,
    IJobEventPublisher events,
    IOptions<ProcessingOptions> options,
    TimeProvider time,
    ILogger<JobRunner> logger)
{
    public async Task RunAsync(ProcessingJob job, CancellationToken stoppingToken)
    {
        using var correlationScope = Correlation.BeginScope(job.CorrelationId ?? Correlation.NewId());
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.JobId });

        // Capture before starting: the job drops its document list once it reaches a terminal state.
        var documents = job.Documents;
        if (!job.TryStart(time.GetUtcNow()))
        {
            logger.LogInformation("Skipping job in status {Status}", job.Status);
            return;
        }

        logger.LogInformation("Job started with {DocumentCount} documents", documents.Count);
        logger.LogDebug("Analyzing with document parallelism {Parallelism}", options.Value.DocParallelismPerJob);
        events.JobStarted(job);

        var startTimestamp = time.GetTimestamp();
        var results = new DocumentResultDto[documents.Count];
        var throttle = new ProgressThrottle(documents.Count, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.CancellationToken);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.DocParallelismPerJob,
            CancellationToken = linked.Token
        };

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, documents.Count), parallelOptions, (i, _) =>
            {
                results[i] = AnalyzeDocument(documents[i]);
                var processed = job.IncrementProcessed();
                if (throttle.ShouldReport(processed))
                {
                    logger.LogDebug("Progress {Processed}/{Total}", processed, documents.Count);
                    events.JobProgress(job);
                }

                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested)
        {
            // The cancel endpoint already moved the job to Cancelled and published the event.
            logger.LogInformation("Job cancelled after {Processed}/{Total} documents", job.ProcessedDocuments, documents.Count);
            return;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Not reported as Failed: the Job Service reconciler will find the job missing and redispatch it.
            logger.LogWarning("Processor shutting down; job abandoned after {Processed}/{Total} documents", job.ProcessedDocuments, documents.Count);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job failed unexpectedly");
            if (job.TryFail("Unexpected processing error", time.GetUtcNow()))
            {
                events.JobFailed(job);
            }
            return;
        }

        var summary = BuildSummary(results, time.GetElapsedTime(startTimestamp));
        if (summary.Succeeded == 0 && summary.TotalDocuments > 0)
        {
            logger.LogError("Job failed: all {Total} documents failed", summary.TotalDocuments);
            if (job.TryFail("All documents failed to process", time.GetUtcNow()))
            {
                events.JobFailed(job);
            }
            return;
        }

        if (job.TryComplete(results, summary, time.GetUtcNow()))
        {
            logger.LogInformation(
                "Job completed: {Succeeded} succeeded, {Failed} failed in {DurationMs} ms",
                summary.Succeeded, summary.Failed, summary.DurationMs);
            events.JobCompleted(job);
        }
    }

    private DocumentResultDto AnalyzeDocument(ProcessDocumentRequest document)
    {
        try
        {
            var analysis = analyzer.Analyze(document.Content);
            return new DocumentResultDto(
                document.Index,
                document.Name,
                DocumentStatus.Succeeded,
                analysis.WordCount,
                analysis.UniqueWordCount,
                analysis.TopWords.Select(w => new WordCountDto(w.Word, w.Count)).ToList(),
                analysis.Sha256,
                analysis.CharacterCount,
                analysis.LineCount,
                analysis.AverageWordLength,
                analysis.LongestWord,
                Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One bad document must not fail the whole job: a Warning, the job itself continues.
            logger.LogWarning(ex, "Document {Index} ({Name}) failed", document.Index, document.Name);
            return new DocumentResultDto(
                document.Index,
                document.Name,
                DocumentStatus.Failed,
                WordCount: 0,
                UniqueWordCount: 0,
                TopWords: [],
                Sha256: null,
                CharacterCount: 0,
                LineCount: 0,
                AverageWordLength: 0,
                LongestWord: null,
                Error: $"Processing error: {ex.GetType().Name}");
        }
    }

    private static ProcessingSummaryDto BuildSummary(IReadOnlyList<DocumentResultDto> results, TimeSpan duration)
    {
        var succeeded = results.Count(r => r.Status == DocumentStatus.Succeeded);
        return new ProcessingSummaryDto(
            TotalDocuments: results.Count,
            Succeeded: succeeded,
            Failed: results.Count - succeeded,
            TotalWords: results.Sum(r => (long)r.WordCount),
            DurationMs: (long)duration.TotalMilliseconds);
    }
}
