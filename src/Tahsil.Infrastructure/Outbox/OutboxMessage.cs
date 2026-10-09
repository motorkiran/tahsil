namespace Tahsil.Infrastructure.Outbox;

/// <summary>
/// A message written in the same transaction as the state change that caused it and delivered afterwards by the
/// outbox dispatcher, so side effects such as emails and webhooks are never lost or sent for changes that were
/// rolled back.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 128;

    public required Guid Id { get; init; }

    public required string Type { get; init; }

    /// <summary>The message serialized as JSON.</summary>
    public required string Payload { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public OutboxMessageStatus Status { get; set; }

    public int Attempts { get; set; }

    /// <summary>When the message is due, or when the lease of the instance handling it runs out.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? LastError { get; set; }
}
