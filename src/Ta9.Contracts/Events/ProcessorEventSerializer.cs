using System.Text.Json;

namespace Ta9.Contracts.Events;

/// <summary>Single place that knows how WS messages are (de)serialized, used by both sides of the socket.</summary>
public static class ProcessorEventSerializer
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ProcessorEventEnvelope Create<TPayload>(
        string type, Guid jobId, TPayload payload, DateTimeOffset occurredAt, string? correlationId) =>
        new(
            CurrentSchemaVersion,
            Guid.NewGuid(),
            type,
            jobId,
            occurredAt,
            correlationId,
            JsonSerializer.SerializeToElement(payload, Options));

    public static byte[] Serialize(ProcessorEventEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    /// <exception cref="JsonException">The message is not a valid envelope.</exception>
    public static ProcessorEventEnvelope Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<ProcessorEventEnvelope>(utf8Json, Options)
        ?? throw new JsonException("Empty event message.");

    public static TPayload GetPayload<TPayload>(ProcessorEventEnvelope envelope) =>
        envelope.Payload.Deserialize<TPayload>(Options)
        ?? throw new JsonException($"Missing payload for {envelope.Type}.");

    public static byte[] SerializeAck(Guid eventId) =>
        JsonSerializer.SerializeToUtf8Bytes(EventAck.For(eventId), Options);

    /// <summary>Returns null when the message is not an ack (or not valid JSON).</summary>
    public static EventAck? TryDeserializeAck(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var ack = JsonSerializer.Deserialize<EventAck>(utf8Json, Options);
            return ack is { Type: EventAck.MessageType } && ack.EventId != Guid.Empty ? ack : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
