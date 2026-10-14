using Tahsil.Domain.Common;

namespace Tahsil.Domain.Payments;

public enum RefundStatus
{
    Processing,
    Succeeded,
    Failed,
}

public enum RefundKind
{
    Refund,

    /// <summary>Canceled before settlement, on the day of the payment.</summary>
    Void,

    /// <summary>Money returned outside Tahsil, for payments recorded out of band.</summary>
    OutOfBand,
}

public enum RefundReason
{
    RequestedByCustomer,
    Duplicate,
    Fraudulent,
    SubscriptionCanceled,
    LateSuccess,
    CardVerification,

    /// <summary>An operator's test payment of a connection, refunded as soon as it is paid (ADP-18).</summary>
    ConnectionTest,
    Other,
}

/// <summary>
/// Money given back from a succeeded payment (PAY-18), written ahead of the provider call like a payment. Refunds never
/// change a payment's or an invoice's status; they raise the amounts refunded (DOM-18, PAY-22).
/// </summary>
public sealed class Refund : AggregateRoot
{
    public const int NoteMaxLength = 500;

    private Refund()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>The ID in 32 lowercase hexadecimal digits, sent to the provider (PAY-7).</summary>
    public string Reference { get; private init; } = string.Empty;

    public Guid PaymentId { get; private init; }

    public long Amount { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public RefundReason Reason { get; private init; }

    public RefundKind Kind { get; private set; }

    public RefundStatus Status { get; private set; }

    public string? ProviderRefundId { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public string? ProviderFailureCode { get; private set; }

    public DateTimeOffset? ReconcileAt { get; private set; }

    public int ReconcileAttempts { get; private set; }

    public string? Note { get; private init; }

    public Actor Actor { get; private init; } = Actor.System;

    public DateTimeOffset? SucceededAt { get; private set; }

    public DateTimeOffset? FailedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Refund Create(Guid paymentId, long amount, string currency, RefundReason reason, string? note, Actor actor, DateTimeOffset reconcileAt, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        var id = Guid.CreateVersion7(now);
        var text = Text.Normalize(note);
        return new Refund
        {
            Id = id,
            Reference = id.ToString("N"),
            PaymentId = paymentId,
            Amount = amount,
            Currency = currency,
            Reason = reason,
            Kind = RefundKind.Refund,
            Status = RefundStatus.Processing,
            Note = text is { Length: > NoteMaxLength } ? text[..NoteMaxLength] : text,
            Actor = actor,
            ReconcileAt = reconcileAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Succeed(string? providerRefundId, RefundKind kind, DateTimeOffset now)
    {
        EnsureProcessing();
        Status = RefundStatus.Succeeded;
        ProviderRefundId = providerRefundId ?? ProviderRefundId;
        Kind = kind;
        SucceededAt = now;
        ReconcileAt = null;
        UpdatedAt = now;
    }

    public void Fail(string code, string? message, string? providerRefundId, DateTimeOffset now)
    {
        EnsureProcessing();
        Status = RefundStatus.Failed;
        FailureCode = code.Length > 32 ? code[..32] : code;
        FailureMessage = message is { Length: > 500 } ? message[..500] : message;
        ProviderRefundId = providerRefundId ?? ProviderRefundId;
        FailedAt = now;
        ReconcileAt = null;
        UpdatedAt = now;
    }

    public void RecordProviderRefundId(string? providerRefundId, DateTimeOffset now)
    {
        ProviderRefundId = providerRefundId ?? ProviderRefundId;
        UpdatedAt = now;
    }

    public void ClaimReconciliation(DateTimeOffset next, DateTimeOffset now)
    {
        ReconcileAttempts++;
        ReconcileAt = next;
        UpdatedAt = now;
    }

    public void StopReconciling(DateTimeOffset now)
    {
        ReconcileAt = null;
        UpdatedAt = now;
    }

    private void EnsureProcessing()
    {
        if (Status != RefundStatus.Processing)
        {
            throw new InvalidOperationException($"The refund {Id} is {Status}, not processing.");
        }
    }
}
