using System.ComponentModel.DataAnnotations;

namespace Tahsil.Contracts;

public enum CheckoutMode
{
    /// <summary>Sells one-time prices.</summary>
    Payment,

    /// <summary>Saves a card without selling anything; needs <c>currency</c>.</summary>
    Setup,

    /// <summary>Sells recurring prices with one interval, and one-time prices on the first invoice.</summary>
    Subscription,
}

public enum PaymentMethodCollectionMode
{
    /// <summary>A trial collects no card; the customer adds one before it ends.</summary>
    IfRequired,

    /// <summary>A trial collects a card, charged when it ends.</summary>
    Always,
}

/// <summary>The subscription a subscription session creates.</summary>
public sealed record CheckoutSubscriptionData
{
    /// <summary>Days of trial, counted from completion; <c>0</c> turns off the prices' default trial.</summary>
    public int? TrialDays { get; init; }

    /// <summary>The end of the trial; takes precedence over <c>trialDays</c>.</summary>
    public DateTimeOffset? TrialEnd { get; init; }

    public PaymentMethodCollectionMode PaymentMethodCollection { get; init; } = PaymentMethodCollectionMode.IfRequired;

    /// <summary>
    /// For a connection that cannot save cards, whose subscriptions are invoiced: days from an invoice's finalization
    /// to its due date, 0 by default.
    /// </summary>
    public int? DaysUntilDue { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// The subscription the session creates: its trial, whether the trial collects a card, and how its invoices are
/// collected (<c>send_invoice</c> when the connection cannot save cards).
/// </summary>
public sealed record CheckoutSubscriptionResponse(
    int? TrialDays,
    DateTimeOffset? TrialEnd,
    PaymentMethodCollectionMode PaymentMethodCollection,
    CollectionMethod CollectionMethod,
    int? DaysUntilDue,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>What each renewal bills: a period of the recurring items, every <c>intervalCount</c> <c>interval</c>s.</summary>
public sealed record CheckoutRecurringResponse(PriceInterval Interval, int IntervalCount, CheckoutQuoteResponse Period);

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

    /// <summary>1 when omitted; none for metered prices.</summary>
    public int? Quantity { get; init; }
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

    /// <summary>What is sold, in one currency; none in setup mode. Metered prices take no quantity.</summary>
    public IReadOnlyList<CheckoutItemRequest>? Items { get; init; }

    /// <summary>For subscription sessions: the subscription to create.</summary>
    public CheckoutSubscriptionData? Subscription { get; init; }

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
    IReadOnlyList<CheckoutQuoteLineResponse> Lines, long Subtotal, long DiscountTotal, long TaxTotal, long TotalExcludingTax, long Total, long AmountDue)
{
    /// <summary>For subscriptions: what each renewal bills.</summary>
    public CheckoutRecurringResponse? Recurring { get; init; }
}

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

    public required CheckoutSubscriptionResponse? Subscription { get; init; }

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

    public required Guid? SubscriptionId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
