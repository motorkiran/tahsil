using System.ComponentModel.DataAnnotations;

namespace Tahsil.Contracts;

public enum CheckoutMode
{
    /// <summary>Sells one-time prices.</summary>
    Payment,

    /// <summary>Saves a card without selling anything; needs <c>currency</c>.</summary>
    Setup,
}

public enum CheckoutStatus
{
    Open,
    Complete,
    Expired,
}

public enum BillingDetailsCollectionMode
{
    /// <summary>The details the provider and invoices need.</summary>
    Auto,

    /// <summary>Always the full address and tax ID.</summary>
    Required,
}

public sealed record CheckoutItemRequest
{
    /// <summary>A price ID or lookup key.</summary>
    [Required]
    public required string Price { get; init; }

    public int Quantity { get; init; } = 1;
}

/// <summary>A customer to create when they submit the checkout form; one with the same external ID is reused.</summary>
public sealed record CheckoutCustomerDetails
{
    public string? ExternalId { get; init; }

    public string? Email { get; init; }

    public string? Name { get; init; }

    public CustomerType Type { get; init; } = CustomerType.Individual;

    public string? Phone { get; init; }

    public CustomerAddress? Address { get; init; }

    public string? Locale { get; init; }

    public KimlikSubscriber? KimlikSubscriber { get; init; }
}

public sealed record CreateCheckoutSessionRequest
{
    public required CheckoutMode Mode { get; init; }

    /// <summary>An existing customer's ID; otherwise <c>customerDetails</c> describe one to create.</summary>
    public Guid? Customer { get; init; }

    public CheckoutCustomerDetails? CustomerDetails { get; init; }

    /// <summary>What is sold, in one currency; none in setup mode.</summary>
    public IReadOnlyList<CheckoutItemRequest>? Items { get; init; }

    /// <summary>Required in setup mode; otherwise the items' currency.</summary>
    public string? Currency { get; init; }

    public bool AllowPromotionCodes { get; init; }

    public BillingDetailsCollectionMode BillingDetailsCollection { get; init; } = BillingDetailsCollectionMode.Auto;

    /// <summary><c>tr</c> or <c>en</c>; the customer's language when omitted.</summary>
    public string? Locale { get; init; }

    /// <summary>Where the customer goes after paying; <c>{CHECKOUT_SESSION_ID}</c> is replaced with the session's ID.</summary>
    [Required]
    public required string SuccessUrl { get; init; }

    public string? CancelUrl { get; init; }

    /// <summary>The application's own reference, such as an order number.</summary>
    public string? ClientReferenceId { get; init; }

    /// <summary>An ISO 8601 duration from <c>PT30M</c> to <c>PT24H</c> (the default).</summary>
    public string? ExpiresIn { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record CheckoutQuoteLineResponse(
    Guid PriceId,
    string Description,
    decimal Quantity,
    decimal? UnitAmountDecimal,
    long Amount,
    long DiscountAmount,
    decimal? TaxPercentage,
    bool? TaxInclusive,
    long TaxAmount,
    long Total);

/// <summary>The first invoice as computed for the customer, which is what they pay; never taken from the browser.</summary>
public sealed record CheckoutQuoteResponse(
    IReadOnlyList<CheckoutQuoteLineResponse> Lines, long Subtotal, long DiscountTotal, long TaxTotal, long TotalExcludingTax, long Total, long AmountDue);

public sealed record CheckoutSessionResponse
{
    public required Guid Id { get; init; }

    /// <summary>The page to send the customer to; returned when the session is created only, since Tahsil keeps just its hash.</summary>
    public required Uri? Url { get; init; }

    public required CheckoutMode Mode { get; init; }

    public required CheckoutStatus Status { get; init; }

    public required Guid? CustomerId { get; init; }

    public required CheckoutCustomerDetails? CustomerDetails { get; init; }

    public required string Currency { get; init; }

    public required CheckoutQuoteResponse Quote { get; init; }

    public required bool AllowPromotionCodes { get; init; }

    public required BillingDetailsCollectionMode BillingDetailsCollection { get; init; }

    public required string? Locale { get; init; }

    public required string SuccessUrl { get; init; }

    public required string? CancelUrl { get; init; }

    public required string? ClientReferenceId { get; init; }

    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset? CompletedAt { get; init; }

    public required DateTimeOffset? ExpiredAt { get; init; }

    public required Guid? InvoiceId { get; init; }

    public required Guid? PaymentId { get; init; }

    public required Guid? PaymentMethodId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
