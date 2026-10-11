using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>A coupon's ID or key to discount the subscription by; or give <see cref="PromotionCode"/>.</summary>
    public string? Coupon { get; init; }

    /// <summary>A promotion code the customer gave, ignoring case.</summary>
    public string? PromotionCode { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public enum ProrationBehavior
{
    /// <summary>Proration lines go on an invoice that is finalized and collected at once.</summary>
    AlwaysInvoice,

    /// <summary>Proration lines wait as pending items for the next renewal invoice.</summary>
    CreateProrations,

    /// <summary>No proration; the new items bill from the next renewal.</summary>
    None,
}

public enum ChangeTiming
{
    Now,

    /// <summary>At the end of the current period, or at <c>effectiveAt</c>.</summary>
    PeriodEnd,
}

/// <summary>
/// JSON Merge Patch. <c>items</c> replaces the item set (omitted items are removed), now or at a renewal;
/// <c>scheduledChange: null</c> drops a scheduled change; <c>coupon</c> or <c>promotionCode</c> replaces the discount,
/// and <c>coupon: null</c> removes it.
/// </summary>
public sealed record UpdateSubscriptionRequest
{
    public IReadOnlyList<SubscriptionItemRequest>? Items { get; init; }

    /// <summary>For changes now outside a trial; <c>always_invoice</c> by default.</summary>
    public ProrationBehavior ProrationBehavior { get; init; } = ProrationBehavior.AlwaysInvoice;

    public ChangeTiming Effective { get; init; } = ChangeTiming.Now;

    /// <summary>With <c>effective: period_end</c>: a later renewal of the subscription to change at, such as one far enough for a price increase notice.</summary>
    public DateTimeOffset? EffectiveAt { get; init; }

    /// <summary>Applies a price increase without notice; for company customers only.</summary>
    public bool SkipNotice { get; init; }

    /// <summary>While trialing: a new end of the trial, or <c>now</c> to end it at once.</summary>
    public string? TrialEnd { get; init; }

    /// <summary>The card to charge; <c>null</c> falls back to the customer's default.</summary>
    public Guid? DefaultPaymentMethod
    {
        get;
        init
        {
            field = value;
            HasDefaultPaymentMethod = true;
        }
    }

    /// <summary>For active subscriptions; applies from the next invoice.</summary>
    public CollectionMethod? CollectionMethod { get; init; }

    /// <summary>With <c>collectionMethod: send_invoice</c>: days from finalization to the due date, 1 to 365.</summary>
    public int? DaysUntilDue { get; init; }

    /// <summary>Only <c>null</c>, which drops the scheduled change.</summary>
    public JsonElement? ScheduledChange
    {
        get;
        init
        {
            field = value;
            HasScheduledChange = true;
        }
    }

    /// <summary>A coupon's ID or key that replaces the discount; <c>null</c> removes it.</summary>
    public string? Coupon
    {
        get;
        init
        {
            field = value;
            HasCoupon = true;
        }
    }

    /// <summary>A promotion code that replaces the discount.</summary>
    public string? PromotionCode { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    [JsonIgnore]
    public bool HasCoupon { get; private init; }

    [JsonIgnore]
    public bool HasDefaultPaymentMethod { get; private init; }

    [JsonIgnore]
    public bool HasScheduledChange { get; private init; }
}

/// <summary>An invoice as it would be created, without being created (SUB-24).</summary>
public sealed record InvoicePreviewResponse
{
    public required BillingReason BillingReason { get; init; }

    public required string Currency { get; init; }

    public required DateTimeOffset? PeriodStart { get; init; }

    public required DateTimeOffset? PeriodEnd { get; init; }

    public required IReadOnlyList<InvoiceLineResponse> Lines { get; init; }

    public required long Subtotal { get; init; }

    public required long DiscountTotal { get; init; }

    public required long TaxTotal { get; init; }

    public required long TotalExcludingTax { get; init; }

    public required long Total { get; init; }

    /// <summary>The customer balance it would apply: positive when credit is spent, negative when a debt joins or the invoice becomes credit.</summary>
    public required long AppliedBalance { get; init; }

    public required long AmountDue { get; init; }
}

public sealed record UpcomingRenewalResponse(DateTimeOffset At, long Amount);

public sealed record SubscriptionPreviewResponse
{
    /// <summary>The invoice the change creates now; none for scheduled changes, <c>create_prorations</c> or <c>none</c>.</summary>
    public required InvoicePreviewResponse? Invoice { get; init; }

    /// <summary>The proration lines that would wait for the next renewal, with <c>create_prorations</c>.</summary>
    public required IReadOnlyList<InvoiceLineResponse> PendingLines { get; init; }

    /// <summary>The next renewal after the change: when, and its total.</summary>
    public required UpcomingRenewalResponse? NextRenewal { get; init; }
}

public enum CancellationRefund
{
    None,

    /// <summary>The unused share of the current period, computed like a proration credit and capped at what its payments can still refund.</summary>
    Prorated,

    /// <summary>The latest paid invoice's payments, in full, such as within a withdrawal period.</summary>
    LastPayment,
}

public sealed record CancelSubscriptionRequest
{
    /// <summary><c>period_end</c> (the default), <c>now</c>, or an RFC 3339 timestamp within the current period.</summary>
    public string At { get; init; } = "period_end";

    /// <summary><c>requested_by_customer</c> or <c>requested_by_merchant</c> (the default).</summary>
    public CancellationReason Reason { get; init; } = CancellationReason.RequestedByMerchant;

    public CancellationFeedback? Feedback { get; init; }

    /// <summary>At most 500 characters.</summary>
    public string? Comment { get; init; }

    /// <summary>Only with <c>at: now</c>; anything but <c>none</c> also needs <c>tahsil.refunds:write</c>.</summary>
    public CancellationRefund Refund { get; init; } = CancellationRefund.None;

    /// <summary>With <c>at: now</c>: bills the subscription's pending items on a final invoice (the default), or deletes them.</summary>
    public bool InvoiceNow { get; init; } = true;
}

public sealed record SubscriptionPriceResponse(Guid Id, string? Key, string? Name, ProductSummary Product);

public sealed record SubscriptionItemResponse(Guid Id, SubscriptionPriceResponse Price, int? Quantity);

public sealed record LatestInvoiceResponse(Guid Id, string? Number, InvoiceStatus Status, long AmountDue, Uri? HostedUrl);

public sealed record ScheduledChangeItemResponse(SubscriptionPriceResponse Price, int? Quantity);

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

    /// <summary>The discount its invoices get while it lasts.</summary>
    public required DiscountResponse? Discount { get; init; }

    /// <summary>When an incomplete subscription expires unless its first invoice is paid.</summary>
    public required DateTimeOffset? IncompleteExpiresAt { get; init; }

    public required DateTimeOffset? PastDueSince { get; init; }

    public required Guid? CheckoutSessionId { get; init; }

    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
