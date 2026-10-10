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

/// <summary>Pays an invoice now with one of the customer's saved cards, off session.</summary>
public sealed record PayInvoiceRequest
{
    public required Guid PaymentMethod { get; init; }
}

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
