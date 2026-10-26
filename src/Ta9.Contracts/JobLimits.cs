namespace Ta9.Contracts;

/// <summary>Input limits shared by the Job Service (public API) and the Processor (internal API).</summary>
public static class JobLimits
{
    public const int MaxDocumentsPerJob = 1_000;
    public const int MaxDocumentBytes = 100 * 1024;
    public const int MaxDocumentNameLength = 256;
    public const int MaxJobNameLength = 200;
}
