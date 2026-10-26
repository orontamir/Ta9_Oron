using Serilog.Events;
using Serilog.Parsing;

namespace Ta9.ServiceDefaults.Logging;

/// <summary>
/// A log event flattened for storage. The ids used to follow a flow (correlation id, job id) are promoted
/// to top-level fields so they can be indexed; every other property is kept as text.
/// </summary>
public sealed record LogRecord(
    DateTimeOffset Timestamp,
    string Level,
    string Service,
    string Host,
    string Message,
    string MessageTemplate,
    string? SourceContext,
    string? CorrelationId,
    string? JobId,
    string? Exception,
    IReadOnlyDictionary<string, string> Properties)
{
    public const int MaxMessageLength = 8 * 1024;
    public const int MaxExceptionLength = 16 * 1024;
    public const int MaxPropertyLength = 1024;

    private static readonly HashSet<string> Promoted = ["SourceContext", "CorrelationId", "JobId", "Service"];

    public static LogRecord From(LogEvent logEvent, string service, string host)
    {
        var properties = new Dictionary<string, string>();
        foreach (var (name, value) in logEvent.Properties)
        {
            if (!Promoted.Contains(name))
            {
                properties[name] = Truncate(Render(value), MaxPropertyLength)!;
            }
        }

        return new LogRecord(
            logEvent.Timestamp.ToUniversalTime(),
            logEvent.Level.ToString(),
            service,
            host,
            Truncate(RenderPlain(logEvent), MaxMessageLength)!,
            logEvent.MessageTemplate.Text,
            Scalar(logEvent, "SourceContext"),
            Scalar(logEvent, "CorrelationId"),
            Scalar(logEvent, "JobId"),
            Truncate(logEvent.Exception?.ToString(), MaxExceptionLength),
            properties);
    }

    /// <summary>Like <c>{Message:lj}</c> on the console: string values without the quotes Serilog's default rendering adds.</summary>
    private static string RenderPlain(LogEvent logEvent)
    {
        using var output = new StringWriter();
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property
                && logEvent.Properties.TryGetValue(property.PropertyName, out var value)
                && value is ScalarValue { Value: string text })
            {
                output.Write(text);
            }
            else
            {
                token.Render(logEvent.Properties, output);
            }
        }

        return output.ToString();
    }

    private static string? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) ? Render(value) : null;

    // Strings without the quotes Serilog adds when rendering; structures and sequences as Serilog renders them.
    private static string Render(LogEventPropertyValue value) =>
        value is ScalarValue scalar ? scalar.Value?.ToString() ?? "null" : value.ToString();

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…[truncated]");
}
