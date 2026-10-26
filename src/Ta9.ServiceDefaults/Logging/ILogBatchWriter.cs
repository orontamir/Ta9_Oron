namespace Ta9.ServiceDefaults.Logging;

/// <summary>Persists a batch of log records. Implementations may throw; the sink handles failures.</summary>
public interface ILogBatchWriter
{
    Task WriteAsync(IReadOnlyList<LogRecord> batch, CancellationToken cancellationToken);
}
