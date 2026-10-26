using System.Collections.Concurrent;
using Serilog.Events;
using Serilog.Parsing;
using Ta9.ServiceDefaults.Logging;

namespace Ta9.ServiceDefaults.Tests.Logging;

internal static class LogTestData
{
    private static readonly MessageTemplateParser Parser = new();

    public static LogEvent Event(
        string template = "hello",
        LogEventLevel level = LogEventLevel.Information,
        Exception? exception = null,
        params (string Name, object? Value)[] properties) =>
        new(DateTimeOffset.UtcNow, level, exception, Parser.Parse(template),
            properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))));

    public static MongoLogOptions Options(int batchSize = 100, int queueCapacity = 1000, TimeSpan? flushInterval = null) => new()
    {
        BatchSize = batchSize,
        QueueCapacity = queueCapacity,
        FlushInterval = flushInterval ?? TimeSpan.FromMilliseconds(20),
        ShutdownTimeout = TimeSpan.FromSeconds(5)
    };

    public static async Task EventuallyAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>Records each batch; can be made to fail or to block, to exercise the sink's failure paths.</summary>
internal sealed class FakeLogWriter : ILogBatchWriter
{
    private readonly ConcurrentQueue<IReadOnlyList<LogRecord>> _batches = new();
    private int _attempts;

    public Func<int, Task>? OnWrite { get; set; }

    public int Attempts => Volatile.Read(ref _attempts);

    public IReadOnlyList<IReadOnlyList<LogRecord>> Batches => _batches.ToArray();

    public IReadOnlyList<LogRecord> Records => _batches.SelectMany(b => b).ToList();

    public async Task WriteAsync(IReadOnlyList<LogRecord> batch, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        var copy = batch.ToList();
        if (OnWrite is not null)
        {
            await OnWrite(attempt);
        }

        _batches.Enqueue(copy);
    }
}
