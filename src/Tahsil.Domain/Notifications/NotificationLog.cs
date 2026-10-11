namespace Tahsil.Domain.Notifications;

public enum NotificationStatus
{
    Sent,

    /// <summary>Not sent on purpose: email is not configured, the type is off, or the customer has no address (NTF-13, NTF-14).</summary>
    Skipped,
}

/// <summary>
/// An email Tahsil sent or chose not to send, once per occurrence (NTF-12): a second intent with the same key sends
/// nothing.
/// </summary>
public sealed class NotificationLog
{
    public const int OccurrenceKeyMaxLength = 200;

    private NotificationLog()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>The email's type, such as <c>order_confirmation</c>.</summary>
    public string Type { get; private init; } = string.Empty;

    /// <summary>What makes this email unique, such as the type and the checkout session's ID.</summary>
    public string OccurrenceKey { get; private init; } = string.Empty;

    public Guid? CustomerId { get; private init; }

    public NotificationStatus Status { get; private init; }

    /// <summary>Why it was skipped.</summary>
    public string? Reason { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static NotificationLog Sent(string type, string occurrenceKey, Guid? customerId, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(now), Type = type, OccurrenceKey = occurrenceKey, CustomerId = customerId, Status = NotificationStatus.Sent, CreatedAt = now };

    public static NotificationLog Skipped(string type, string occurrenceKey, Guid? customerId, string reason, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(now), Type = type, OccurrenceKey = occurrenceKey, CustomerId = customerId, Status = NotificationStatus.Skipped, Reason = reason, CreatedAt = now };
}
