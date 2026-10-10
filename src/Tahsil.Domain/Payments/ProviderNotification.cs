namespace Tahsil.Domain.Payments;

public enum ProviderNotificationStatus
{
    Pending,
    Processed,

    /// <summary>None of its references is a payment or refund of Tahsil's, as when another system shares the provider account.</summary>
    Ignored,
    Failed,
}

/// <summary>
/// A verified notification a provider sent (PAY-23), stored once per provider event and processed later (PAY-24). It
/// only settles payments and refunds Tahsil wrote ahead; it never creates one (PAY-25).
/// </summary>
public sealed class ProviderNotification
{
    public const int EventIdMaxLength = 200;
    public const int TypeMaxLength = 100;
    public const int LastErrorMaxLength = 2000;
    public const int MaxAttempts = 10;

    private ProviderNotification()
    {
    }

    public Guid Id { get; private init; }

    public Guid ConnectionId { get; private init; }

    public string? ProviderEventId { get; private init; }

    public string Type { get; private init; } = string.Empty;

    /// <summary>What the adapter read from it, as JSON; never secrets.</summary>
    public string Payload { get; private init; } = "[]";

    /// <summary>The body as received, encrypted; cleared after 30 days.</summary>
    public byte[]? RawBody { get; private set; }

    public ProviderNotificationStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset ReceivedAt { get; private init; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public static ProviderNotification Receive(Guid connectionId, string? eventId, string? type, string payload, Func<Guid, byte[]> encryptBody, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7(now);
        return new ProviderNotification
        {
            Id = id,
            ConnectionId = connectionId,
            ProviderEventId = eventId is { Length: > EventIdMaxLength } ? eventId[..EventIdMaxLength] : eventId,
            Type = type is null ? "unknown" : type.Length > TypeMaxLength ? type[..TypeMaxLength] : type,
            Payload = payload,
            RawBody = encryptBody(id),
            Status = ProviderNotificationStatus.Pending,
            NextAttemptAt = now,
            ReceivedAt = now,
        };
    }

    public void Complete(bool applied, DateTimeOffset now)
    {
        Status = applied ? ProviderNotificationStatus.Processed : ProviderNotificationStatus.Ignored;
        ProcessedAt = now;
        NextAttemptAt = null;
        LastError = null;
    }

    /// <summary>An attempt failed: it is retried after <paramref name="retryAt"/>, or fails for good after ten attempts.</summary>
    public bool RecordFailure(string error, DateTimeOffset retryAt)
    {
        Attempts++;
        LastError = error.Length > LastErrorMaxLength ? error[..LastErrorMaxLength] : error;
        if (Attempts >= MaxAttempts)
        {
            Status = ProviderNotificationStatus.Failed;
            NextAttemptAt = null;
            return true;
        }

        NextAttemptAt = retryAt;
        return false;
    }
}
