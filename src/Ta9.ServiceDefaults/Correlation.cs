using Serilog.Context;

namespace Ta9.ServiceDefaults;

/// <summary>
/// Ambient correlation id that flows with the async call chain (HTTP request, background job, WS event)
/// and is attached to every log line written inside the scope.
/// </summary>
public static class Correlation
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 64;

    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? CurrentId => CurrentValue.Value;

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Accepts only short, header/log-safe ids so a caller cannot forge log lines.</summary>
    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= MaxLength
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static IDisposable BeginScope(string correlationId)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = correlationId;
        var logScope = LogContext.PushProperty("CorrelationId", correlationId);
        return new Scope(previous, logScope);
    }

    private sealed class Scope(string? previous, IDisposable logScope) : IDisposable
    {
        public void Dispose()
        {
            logScope.Dispose();
            CurrentValue.Value = previous;
        }
    }
}
