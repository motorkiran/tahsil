namespace Tahsil.Contracts;

public enum PaymentStatus
{
    Processing,
    RequiresAction,
    Succeeded,
    Failed,
    Canceled,
}

public enum PaymentInitiator
{
    Customer,
    Merchant,
    Operator,
}

public enum PaymentMethodKind
{
    Card,
    BankTransfer,
    Cash,
    Other,
}

public enum PaymentPurpose
{
    Charge,
    Setup,
}

public enum OutOfBandMethod
{
    BankTransfer,
    Cash,
    Other,
}

/// <summary>Money received outside Tahsil, recorded against an invoice (INV-14).</summary>
public sealed record OutOfBandPaymentRequest
{
    public OutOfBandMethod Method { get; init; } = OutOfBandMethod.BankTransfer;

    /// <summary>Minor units; what is left to pay when omitted. A smaller amount is a partial payment.</summary>
    public long? Amount { get; init; }

    /// <summary>When the money arrived; now when omitted.</summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>How the payment is identified, such as a transfer's reference; at most 100 characters.</summary>
    public string? Reference { get; init; }

    /// <summary>At most 500 characters.</summary>
    public string? Note { get; init; }
}

/// <summary>Pays an invoice now: with one of the customer's saved cards, off session, or by recording money received outside Tahsil.</summary>
public sealed record PayInvoiceRequest
{
    /// <summary>A saved card of the customer to charge now.</summary>
    public Guid? PaymentMethod { get; init; }

    /// <summary>Money received outside Tahsil, such as a bank transfer.</summary>
    public OutOfBandPaymentRequest? OutOfBand { get; init; }
}

public sealed record OutOfBandResponse(DateTimeOffset ReceivedAt, string? Reference, string? Note);

public sealed record PaymentResponse
{
    public required Guid Id { get; init; }

    /// <summary>What the provider knows the payment by.</summary>
    public required string Reference { get; init; }

    public required Guid? InvoiceId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid? ConnectionId { get; init; }

    public required Guid? PaymentMethodId { get; init; }

    public required long Amount { get; init; }

    public required string Currency { get; init; }

    public required PaymentPurpose Purpose { get; init; }

    public required PaymentStatus Status { get; init; }

    public required PaymentInitiator Initiator { get; init; }

    public required PaymentMethodKind Method { get; init; }

    /// <summary>For payments received outside Tahsil.</summary>
    public required OutOfBandResponse? OutOfBand { get; init; }

    public required string? ProviderPaymentId { get; init; }

    public required bool? ThreeDSecure { get; init; }

    /// <summary>A normalized code such as <c>insufficient_funds</c>.</summary>
    public required string? FailureCode { get; init; }

    /// <summary><c>retry</c>, <c>new_payment_method</c>, <c>authenticate</c> or <c>merchant</c>.</summary>
    public required string? FailureCategory { get; init; }

    /// <summary>A message safe to show the customer.</summary>
    public required string? FailureMessage { get; init; }

    public required string? ProviderFailureCode { get; init; }

    public required string? ProviderFailureMessage { get; init; }

    public required long AmountRefunded { get; init; }

    public required DateTimeOffset? ExpiresAt { get; init; }

    public required DateTimeOffset? SucceededAt { get; init; }

    public required DateTimeOffset? FailedAt { get; init; }

    public required DateTimeOffset? CanceledAt { get; init; }

    public required ActorResponse Actor { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public enum RefundStatus
{
    Processing,
    Succeeded,
    Failed,
}

public enum RefundKind
{
    Refund,
    Void,
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
    ConnectionTest,
    Other,
}

public sealed record CreateRefundRequest
{
    public required Guid Payment { get; init; }

    /// <summary>Minor units; what is left to refund of the payment when omitted.</summary>
    public long? Amount { get; init; }

    public RefundReason Reason { get; init; } = RefundReason.RequestedByCustomer;

    public string? Note { get; init; }
}

public sealed record RefundResponse(
    Guid Id,
    string Reference,
    Guid PaymentId,
    long Amount,
    string Currency,
    RefundReason Reason,
    RefundKind Kind,
    RefundStatus Status,
    string? ProviderRefundId,
    string? FailureCode,
    string? FailureMessage,
    string? Note,
    ActorResponse Actor,
    DateTimeOffset? SucceededAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Something an operator must look at, such as an amount mismatch or a payment reconciliation could not settle.</summary>
public sealed record AttentionItemResponse(
    Guid Id,
    string Kind,
    string SubjectType,
    Guid SubjectId,
    Guid? CustomerId,
    IReadOnlyDictionary<string, string> Details,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution,
    ActorResponse? ResolvedBy);

/// <summary>A worker item that failed ten times in a row (ENG-6).</summary>
public sealed record WorkerAttentionResponse(string Worker, Guid ItemId, int FailureCount, DateTimeOffset FirstFailedAt, DateTimeOffset LastFailedAt, string? LastError);

/// <summary>An operator's ruling on a payment with the provider's evidence (ENG-16): it succeeded or it failed.</summary>
public sealed record ResolvePaymentRequest
{
    public required bool Succeeded { get; init; }

    /// <summary>The provider's ID of the payment, for a success.</summary>
    public string? ProviderPaymentId { get; init; }

    /// <summary>What the evidence was, for the audit log.</summary>
    public required string Note { get; init; }
}
