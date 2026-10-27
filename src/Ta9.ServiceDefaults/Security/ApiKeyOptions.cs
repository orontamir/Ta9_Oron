namespace Ta9.ServiceDefaults.Security;

/// <summary>
/// Configuration section <c>Auth</c>. The keys are never stored in the repository: they come from environment
/// variables (<c>Auth__ReaderKey</c>, …), generated per installation by <c>scripts/keys.ps1</c> / <c>scripts/keys.sh</c>.
/// </summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Auth";

    /// <summary>Only for local experiments: when false every request is allowed (a Warning is logged at startup).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Client key with read access (GET jobs, results, SSE, audit entries).</summary>
    public string? ReaderKey { get; set; }

    /// <summary>Client key with full access (create, cancel, delete jobs; everything a reader can do).</summary>
    public string? OperatorKey { get; set; }

    /// <summary>The Job Service's own key: presented to the Processor (dispatch) and the Audit Service (summaries).</summary>
    public string? JobServiceKey { get; set; }

    /// <summary>The Processor's own key: presented on the WebSocket connection to the Job Service.</summary>
    public string? ProcessorServiceKey { get; set; }
}
