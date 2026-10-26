using System.Text.Json.Serialization;

namespace Ta9.Contracts.Processing;

// Contracts of the Processor REST API (/api/process), shared by the Processor and its client (Job Service).
// Enums carry their converter so they are always serialized as strings, whatever the host's JSON options.

[JsonConverter(typeof(JsonStringEnumConverter<ProcessingStatus>))]
public enum ProcessingStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<DocumentStatus>))]
public enum DocumentStatus
{
    Succeeded,
    Failed
}

public sealed record ProcessDocumentRequest(int Index, string Name, string Content);

public sealed record ProcessJobRequest(Guid JobId, IReadOnlyList<ProcessDocumentRequest> Documents);

public sealed record WordCountDto(string Word, int Count);

public sealed record DocumentResultDto(
    int Index,
    string Name,
    DocumentStatus Status,
    int WordCount,
    int UniqueWordCount,
    IReadOnlyList<WordCountDto> TopWords,
    string? Sha256,
    int CharacterCount,
    int LineCount,
    double AverageWordLength,
    string? LongestWord,
    string? Error);

public sealed record ProcessingSummaryDto(
    int TotalDocuments,
    int Succeeded,
    int Failed,
    long TotalWords,
    long DurationMs);

/// <summary>Processor view of a job. <see cref="Results"/> is populated only for completed jobs when requested.</summary>
public sealed record ProcessJobStatusResponse(
    Guid JobId,
    ProcessingStatus Status,
    int TotalDocuments,
    int ProcessedDocuments,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    ProcessingSummaryDto? Summary,
    IReadOnlyList<DocumentResultDto>? Results);
