namespace Tahsil.Contracts;

public enum PaymentMethodStatus
{
    Active,
    Expired,
    Removed,
}

/// <summary>A saved card, with what may be shown about it; never card data.</summary>
public sealed record PaymentMethodResponse(
    Guid Id,
    Guid CustomerId,
    Guid ConnectionId,
    string Type,
    PaymentMethodStatus Status,
    bool IsDefault,
    string Brand,
    string Funding,
    string? Bin,
    string Last4,
    int? ExpMonth,
    int? ExpYear,
    DateTimeOffset? ExpiresAt,
    string? Issuer,
    string? CardFamily,
    string? Country,
    DateTimeOffset? RemovedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SetDefaultPaymentMethodRequest
{
    public required Guid PaymentMethod { get; init; }
}

/// <summary>Test mode only: a saved simulator card for a customer, without a browser (SIM-7).</summary>
public sealed record CreateSimulatorPaymentMethodRequest
{
    public required Guid Customer { get; init; }

    /// <summary>A simulator test card number, such as <c>4242424242424242</c>.</summary>
    public required string Card { get; init; }

    /// <summary>12 when omitted.</summary>
    public int? ExpMonth { get; init; }

    /// <summary>Four years from now when omitted.</summary>
    public int? ExpYear { get; init; }
}

/// <summary>
/// Test mode only: how a saved simulator card answers from now on (SIM-8): <c>succeed</c>, a failure code such as
/// <c>insufficient_funds</c>, <c>timeout_after_charge</c>, <c>timeout_before_charge</c> or <c>pending</c>.
/// </summary>
public sealed record UpdateSimulatorPaymentMethodRequest
{
    public required string Behavior { get; init; }
}
