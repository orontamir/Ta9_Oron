using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ta9.ServiceDefaults.Logging;

namespace Ta9.Tests.Shared;

/// <summary>One captured <see cref="ILogger"/> call: level, rendered message, exception and the ambient scope (e.g. JobId).</summary>
public sealed record LogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, object?> Scope);

/// <summary>In-memory <see cref="ILogger{T}"/> for unit tests that assert what a class logs.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly AsyncLocal<ScopeNode?> _scope = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public IEnumerable<LogEntry> AtLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var node = new ScopeNode(state, _scope.Value, this);
        _scope.Value = node;
        return node;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var scope = new Dictionary<string, object?>();
        for (var node = _scope.Value; node is not null; node = node.Parent)
        {
            if (node.State is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    scope.TryAdd(key, value);
                }
            }
        }

        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value)
            : new Dictionary<string, object?>();
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception, properties, scope));
    }

    private sealed class ScopeNode(object state, ScopeNode? parent, ListLogger<T> owner) : IDisposable
    {
        public object State => state;
        public ScopeNode? Parent => parent;
        public void Dispose() => owner._scope.Value = parent;
    }
}

/// <summary>
/// Stands in for MongoDB behind the service's real logging pipeline (Serilog → BatchingLogSink → writer),
/// so host-level tests can assert on exactly the records that would be stored.
/// </summary>
public sealed class CapturingLogWriter : ILogBatchWriter
{
    private readonly ConcurrentQueue<LogRecord> _records = new();

    public IReadOnlyList<LogRecord> Records => _records.ToArray();

    public Task WriteAsync(IReadOnlyList<LogRecord> batch, CancellationToken cancellationToken)
    {
        foreach (var record in batch)
        {
            _records.Enqueue(record);
        }

        return Task.CompletedTask;
    }

    /// <summary>Enables Mongo logging in a test host with this writer instead of a database (fast flush).</summary>
    public void AttachTo(IWebHostBuilder builder)
    {
        builder.UseSetting("Logging:Mongo:Enabled", "true");
        builder.UseSetting("Logging:Mongo:FlushInterval", "00:00:00.050");
        builder.ConfigureTestServices(services => services.AddSingleton<ILogBatchWriter>(this));
    }

    /// <summary>Waits until at least one record matches (records are flushed in the background).</summary>
    public async Task<IReadOnlyList<LogRecord>> WaitForAsync(Func<LogRecord, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var matches = Records.Where(predicate).ToList();
            if (matches.Count > 0)
            {
                return matches;
            }

            if (DateTime.UtcNow > deadline)
            {
                var recent = string.Join(Environment.NewLine, Records.TakeLast(20).Select(r => $"  {r.Level} {r.Service}: {r.Message}"));
                throw new TimeoutException($"No matching log record. Last records:{Environment.NewLine}{recent}");
            }

            await Task.Delay(25);
        }
    }
}
