using System.Text.Json;
using Ta9.Contracts.Processing;

namespace Ta9.Contracts.Events;

// WebSocket contract: Processor → Job Service events, Job Service → Processor acks.
// Every message is one JSON text frame.

public static class ProcessorEventTypes
{
    public const string JobStarted = "JobStarted";
    public const string JobProgress = "JobProgress";
    public const string JobCompleted = "JobCompleted";
    public const string JobFailed = "JobFailed";
    public const string JobCancelled = "JobCancelled";

    /// <summary>Events after which nothing else is expected for the job.</summary>
    public static bool IsTerminal(string type) => type is JobCompleted or JobFailed or JobCancelled;
}

/// <summary>Common envelope. <see cref="Payload"/> shape depends on <see cref="Type"/>.</summary>
public sealed record ProcessorEventEnvelope(
    int SchemaVersion,
    Guid EventId,
    string Type,
    Guid JobId,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    JsonElement Payload);

public sealed record JobStartedPayload(int TotalDocuments);

public sealed record JobProgressPayload(int ProcessedDocuments, int TotalDocuments);

public sealed record JobCompletedPayload(ProcessingSummaryDto Summary, IReadOnlyList<DocumentResultDto> Results);

public sealed record JobFailedPayload(string ErrorCode, string Reason);

public sealed record JobCancelledPayload;

/// <summary>Sent by the Job Service once an event has been handled (persisted or deliberately ignored).</summary>
public sealed record EventAck(string Type, Guid EventId)
{
    public const string MessageType = "Ack";

    public static EventAck For(Guid eventId) => new(MessageType, eventId);
}
