namespace Ta9.ServiceDefaults.Security;

/// <summary>Roles carried by an authenticated caller.</summary>
public static class Roles
{
    public const string Reader = "reader";
    public const string Operator = "operator";
    public const string Service = "service";
}

/// <summary>Authorization policies used by the endpoints.</summary>
public static class AuthPolicies
{
    /// <summary>Read jobs, results, live status and audit entries: reader or operator.</summary>
    public const string Read = "read";

    /// <summary>Create, cancel and delete jobs: operator.</summary>
    public const string Write = "write";

    /// <summary>Service-to-service calls (dispatch, events, audit summaries).</summary>
    public const string Service = "service";
}

/// <summary>Every identity that can hold a key. Each service declares which of them it accepts (least privilege).</summary>
public enum Caller
{
    Reader,
    Operator,
    JobService,
    ProcessorService
}

public sealed record CallerIdentity(Caller Caller, string Name, string Role)
{
    public static CallerIdentity For(Caller caller) => caller switch
    {
        Caller.Reader => new(caller, "reader", Roles.Reader),
        Caller.Operator => new(caller, "operator", Roles.Operator),
        Caller.JobService => new(caller, "job-service", Roles.Service),
        Caller.ProcessorService => new(caller, "processor-service", Roles.Service),
        _ => throw new ArgumentOutOfRangeException(nameof(caller), caller, null)
    };

    public string? KeyFrom(ApiKeyOptions options) => Caller switch
    {
        Caller.Reader => options.ReaderKey,
        Caller.Operator => options.OperatorKey,
        Caller.JobService => options.JobServiceKey,
        Caller.ProcessorService => options.ProcessorServiceKey,
        _ => null
    };
}
