namespace Tahsil.Infrastructure.Outbox;

public enum OutboxMessageStatus
{
    Pending,
    Processed,

    /// <summary>Delivery failed on every attempt; the message is kept for inspection and can be retried.</summary>
    Failed,
}
