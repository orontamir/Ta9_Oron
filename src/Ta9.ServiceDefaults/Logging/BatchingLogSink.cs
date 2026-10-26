using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;

namespace Ta9.ServiceDefaults.Logging;

/// <summary>
/// Serilog sink that hands events to a background writer in batches.
/// <list type="bullet">
/// <item><see cref="Emit"/> never blocks and never throws: it only puts the event in a bounded buffer.</item>
/// <item>A single background loop writes a batch when it is full or after <see cref="MongoLogOptions.FlushInterval"/>.</item>
/// <item>If the buffer is full or a write fails, the events are dropped and counted; the next successful batch
/// carries a Warning with the count, so the gap is visible in the stored logs.</item>
/// <item>Disposing (host shutdown) flushes what is left, waiting at most <see cref="MongoLogOptions.ShutdownTimeout"/>.</item>
/// </list>
/// Logging must never take the service down, so the sink reports its own problems to stderr, never to the logger.
/// </summary>
public sealed class BatchingLogSink : ILogEventSink, IDisposable
{
    public const string DiagnosticPrefix = "[ta9-log-sink]";

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromSeconds(30);

    private readonly ILogBatchWriter _writer;
    private readonly string _service;
    private readonly string _host;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _shutdownTimeout;
    private readonly TextWriter _diagnostics;
    private readonly Channel<LogEvent> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private long _dropped;
    private long _droppedReported;
    private DateTimeOffset _lastDiagnostic = DateTimeOffset.MinValue;
    private int _disposed;

    public BatchingLogSink(ILogBatchWriter writer, string service, MongoLogOptions options, TextWriter? diagnostics = null)
    {
        _writer = writer;
        _service = service;
        _host = Environment.MachineName;
        _batchSize = Math.Max(1, options.BatchSize);
        _flushInterval = options.FlushInterval;
        _shutdownTimeout = options.ShutdownTimeout;
        _diagnostics = diagnostics ?? Console.Error;
        _queue = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(Math.Max(1, options.QueueCapacity))
        {
            // Wait mode makes TryWrite return false when full (the Drop* modes report success while dropping).
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Events that were not stored (buffer full, write failed, or emitted after disposal).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Emit(LogEvent logEvent)
    {
        if (!_queue.Writer.TryWrite(logEvent))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _stopping.Cancel(); // skip the flush delay: write what is left now
        if (!_worker.Wait(_shutdownTimeout))
        {
            Diagnose($"shutdown timeout ({_shutdownTimeout.TotalSeconds:0}s) reached; {_queue.Reader.Count} log events were not stored", force: true);
        }
    }

    private async Task RunAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<LogRecord>(_batchSize + 1);
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (reader.Count < _batchSize && !_stopping.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_flushInterval, _stopping.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down: flush immediately.
                    }
                }

                var notice = AddDroppedNotice(batch);
                while (batch.Count < _batchSize + (notice > 0 ? 1 : 0) && reader.TryRead(out var logEvent))
                {
                    batch.Add(LogRecord.From(logEvent, _service, _host));
                }

                await WriteAsync(batch, notice).ConfigureAwait(false);
                batch.Clear();
            }
        }
        catch (Exception ex)
        {
            Diagnose($"log writer loop stopped: {ex.GetType().Name}: {ex.Message}", force: true);
        }
    }

    /// <summary>Adds a Warning record for events dropped since the last stored notice. Returns the count it reports.</summary>
    private long AddDroppedNotice(List<LogRecord> batch)
    {
        var unreported = Interlocked.Read(ref _dropped) - _droppedReported;
        if (unreported <= 0)
        {
            return 0;
        }

        batch.Add(new LogRecord(
            DateTimeOffset.UtcNow,
            nameof(LogEventLevel.Warning),
            _service,
            _host,
            $"{unreported} log events were not stored (buffer full or log database unavailable)",
            "{DroppedCount} log events were not stored (buffer full or log database unavailable)",
            typeof(BatchingLogSink).FullName,
            CorrelationId: null,
            JobId: null,
            Exception: null,
            new Dictionary<string, string> { ["DroppedCount"] = unreported.ToString() }));
        return unreported;
    }

    private async Task WriteAsync(List<LogRecord> batch, long reportedDrops)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await _writer.WriteAsync(batch, timeout.Token).ConfigureAwait(false);
            _droppedReported += reportedDrops;
        }
        catch (Exception ex)
        {
            var lost = batch.Count - (reportedDrops > 0 ? 1 : 0);
            Interlocked.Add(ref _dropped, lost);
            Diagnose($"could not store {lost} log events: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Diagnose(string message, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastDiagnostic < DiagnosticInterval)
        {
            return; // at most one line per interval while the database is down
        }

        _lastDiagnostic = now;
        try
        {
            _diagnostics.WriteLine($"{DiagnosticPrefix} {now:O} {_service}: {message}");
        }
        catch
        {
            // Nothing else to report to.
        }
    }
}
