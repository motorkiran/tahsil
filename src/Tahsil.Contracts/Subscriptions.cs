using System.ComponentModel.DataAnnotations;

namespace Tahsil.Contracts;

public enum SubscriptionStatus
{
    /// <summary>Created through the API while its first invoice is unpaid; it expires unpaid after a day.</summary>
    Incomplete,

    /// <summary>The first invoice was not paid in time (final).</summary>
    IncompleteExpired,

    Trialing,

    Active,

    /// <summary>An invoice is unpaid past its payment or due date.</summary>
    PastDue,

    /// <summary>Ended (final).</summary>
    Canceled,
}

public enum TrialEndBehavior
{
    /// <summary>A trial that ends without a card cancels the subscription.</summary>
    Cancel,

    /// <summary>A trial that ends without a card still invoices, and the customer is asked for a card.</summary>
    CreateInvoice,
}

public enum CancellationReason
{
    RequestedByCustomer,
    RequestedByMerchant,
    PaymentFailed,
    CustomerDeleted,
    KimlikSubscriberDeleted,
    IncompleteExpired,
    TrialWithoutPaymentMethod,
}

public enum CancellationFeedback
{
    TooExpensive,
    MissingFeatures,
    SwitchedService,
    Unused,
    CustomerService,
    TooComplex,
    LowQuality,
    Other,
}

public sealed record SubscriptionItemRequest
{
    /// <summary>A recurring price, by ID or lookup key.</summary>
    [Required]
    public required string Price { get; init; }

    /// <summary>For licensed prices, 1 when omitted; metered prices take none.</summary>
    public int? Quantity { get; init; }
}

public sealed record CreateSubscriptionRequest
{
    public required Guid Customer { get; init; }

    /// <summary>Recurring prices in one currency with the same interval, at most 20.</summary>
    [Required]
    public required IReadOnlyList<SubscriptionItemRequest> Items { get; init; }

    public CollectionMethod CollectionMethod { get; init; } = CollectionMethod.ChargeAutomatically;

    /// <summary>
    /// The card to charge; the customer's default when omitted. Needed for <c>charge_automatically</c> without a
    /// trial: the first invoice is charged during the request.
    /// </summary>
    public Guid? DefaultPaymentMethod { get; init; }

    /// <summary>Days of trial; <c>0</c> turns off the prices' default trial.</summary>
    public int? TrialDays { get; init; }

    /// <summary>The end of the trial; takes precedence over <c>trialDays</c>.</summary>
    public DateTimeOffset? TrialEnd { get; init; }

    public TrialEndBehavior TrialEndBehavior { get; init; } = TrialEndBehavior.Cancel;

    /// <summary>For <c>send_invoice</c>: days from an invoice's finalization to its due date, 1 to 365.</summary>
    public int? DaysUntilDue { get; init; }

    /// <summary>
    /// A later start of the billing cycle without a trial, at most one interval away, such as the first of the next
    /// month; the time until it is billed as a shorter first period.
    /// </summary>
    public DateTimeOffset? BillingCycleAnchor { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record SubscriptionPriceResponse(Guid Id, string? Key, string? Name, ProductSummary Product);

public sealed record SubscriptionItemResponse(Guid Id, SubscriptionPriceResponse Price, int? Quantity);

public sealed record LatestInvoiceResponse(Guid Id, string? Number, InvoiceStatus Status, long AmountDue, Uri? HostedUrl);

public sealed record ScheduledChangeItemResponse(Guid Price, int? Quantity);

public sealed record ScheduledChangeResponse(IReadOnlyList<ScheduledChangeItemResponse> Items, DateTimeOffset EffectiveAt);

public sealed record SubscriptionResponse
{
    public required Guid Id { get; init; }

    public required Guid CustomerId { get; init; }

    public required SubscriptionStatus Status { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyList<SubscriptionItemResponse> Items { get; init; }

    public required CollectionMethod CollectionMethod { get; init; }

    public required int? DaysUntilDue { get; init; }

    public required Guid? DefaultPaymentMethodId { get; init; }

    public required DateTimeOffset BillingCycleAnchor { get; init; }

    public required DateTimeOffset CurrentPeriodStart { get; init; }

    public required DateTimeOffset CurrentPeriodEnd { get; init; }

    public required DateTimeOffset? TrialStart { get; init; }

    public required DateTimeOffset? TrialEnd { get; init; }

    public required TrialEndBehavior TrialEndBehavior { get; init; }

    public required bool CancelAtPeriodEnd { get; init; }

    public required DateTimeOffset? CancelAt { get; init; }

    public required DateTimeOffset? CanceledAt { get; init; }

    public required DateTimeOffset? EndedAt { get; init; }

    public required CancellationReason? CancellationReason { get; init; }

    public required CancellationFeedback? CancellationFeedback { get; init; }

    public required string? CancellationComment { get; init; }

    public required ScheduledChangeResponse? ScheduledChange { get; init; }

    public required LatestInvoiceResponse? LatestInvoice { get; init; }

    /// <summary>When an incomplete subscription expires unless its first invoice is paid.</summary>
    public required DateTimeOffset? IncompleteExpiresAt { get; init; }

    public required DateTimeOffset? PastDueSince { get; init; }

    public required Guid? CheckoutSessionId { get; init; }

    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
