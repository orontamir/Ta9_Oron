using Ta9.Contracts.Processing;

namespace Ta9.ProcessorService.Application;

/// <summary>
/// In-memory state of one job inside the Processor. All state transitions are guarded by a lock so the
/// worker, the cancel endpoint and readers never observe or create an inconsistent state.
/// </summary>
public sealed class ProcessingJob
{
    private static readonly IReadOnlyList<ProcessDocumentRequest> NoDocuments = [];

    private readonly object _gate = new();
    // Not disposed on purpose: no timer is attached, and the job may still be read after it finishes.
    private readonly CancellationTokenSource _cancellation = new();
    private IReadOnlyList<ProcessDocumentRequest> _documents;
    private int _processedDocuments;
    private ProcessingStatus _status = ProcessingStatus.Queued;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private string? _failureReason;
    private ProcessingSummaryDto? _summary;
    private IReadOnlyList<DocumentResultDto>? _results;

    public ProcessingJob(Guid jobId, IReadOnlyList<ProcessDocumentRequest> documents, string? correlationId, DateTimeOffset acceptedAt)
    {
        JobId = jobId;
        _documents = documents;
        TotalDocuments = documents.Count;
        CorrelationId = correlationId;
        AcceptedAt = acceptedAt;
    }

    public Guid JobId { get; }
    public string? CorrelationId { get; }
    public int TotalDocuments { get; }
    public DateTimeOffset AcceptedAt { get; }
    public CancellationToken CancellationToken => _cancellation.Token;
    public int ProcessedDocuments => Volatile.Read(ref _processedDocuments);

    public IReadOnlyList<ProcessDocumentRequest> Documents { get { lock (_gate) return _documents; } }
    public ProcessingStatus Status { get { lock (_gate) return _status; } }
    public DateTimeOffset? CompletedAt { get { lock (_gate) return _completedAt; } }
    public string? FailureReason { get { lock (_gate) return _failureReason; } }
    public ProcessingSummaryDto? Summary { get { lock (_gate) return _summary; } }
    public IReadOnlyList<DocumentResultDto>? Results { get { lock (_gate) return _results; } }

    public bool IsTerminal { get { lock (_gate) return IsTerminalStatus(_status); } }

    public bool TryStart(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_status != ProcessingStatus.Queued)
            {
                return false;
            }

            _status = ProcessingStatus.Processing;
            _startedAt = now;
            return true;
        }
    }

    public int IncrementProcessed() => Interlocked.Increment(ref _processedDocuments);

    public bool TryComplete(IReadOnlyList<DocumentResultDto> results, ProcessingSummaryDto summary, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_status != ProcessingStatus.Processing)
            {
                return false;
            }

            _status = ProcessingStatus.Completed;
            _results = results;
            _summary = summary;
            _completedAt = now;
            _documents = NoDocuments;
            return true;
        }
    }

    public bool TryFail(string reason, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (IsTerminalStatus(_status))
            {
                return false;
            }

            _status = ProcessingStatus.Failed;
            _failureReason = reason;
            _completedAt = now;
            _documents = NoDocuments;
            return true;
        }
    }

    public bool TryCancel(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (IsTerminalStatus(_status))
            {
                return false;
            }

            _status = ProcessingStatus.Cancelled;
            _completedAt = now;
            _documents = NoDocuments;
        }

        // Outside the lock: Cancel() runs registered callbacks synchronously.
        _cancellation.Cancel();
        return true;
    }

    public ProcessJobStatusResponse ToResponse(bool includeResults)
    {
        lock (_gate)
        {
            return new ProcessJobStatusResponse(
                JobId,
                _status,
                TotalDocuments,
                ProcessedDocuments,
                AcceptedAt,
                _startedAt,
                _completedAt,
                _failureReason,
                _summary,
                includeResults ? _results : null);
        }
    }

    private static bool IsTerminalStatus(ProcessingStatus status) =>
        status is ProcessingStatus.Completed or ProcessingStatus.Failed or ProcessingStatus.Cancelled;
}
