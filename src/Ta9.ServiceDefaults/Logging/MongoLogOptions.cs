using Serilog.Events;

namespace Ta9.ServiceDefaults.Logging;

/// <summary>Configuration section <c>Logging:Mongo</c>: where and how log events are persisted.</summary>
public sealed class MongoLogOptions
{
    public const string SectionName = "Logging:Mongo";

    /// <summary>Off by default so tests and tools never need a database; appsettings / docker-compose turn it on.</summary>
    public bool Enabled { get; set; }

    public string ConnectionString { get; set; } = "mongodb://localhost:27017";

    /// <summary>One database for all services; every record carries its <c>service</c>.</summary>
    public string Database { get; set; } = "ta9_logs";

    public string Collection { get; set; } = "logs";

    /// <summary>Debug and above by default: the database keeps the detail needed to investigate a crash.</summary>
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Debug;

    /// <summary>TTL index: MongoDB deletes records older than this automatically.</summary>
    public int RetentionDays { get; set; } = 7;

    public int BatchSize { get; set; } = 100;

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Bounded buffer between the application and the database; when full, new events are dropped (and counted).</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>How long disposing the logger waits for pending events to be written.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
