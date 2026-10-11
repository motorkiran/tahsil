using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Tahsil.Contracts;

public enum InvoiceStatus
{
    Draft,
    Open,
    Paid,
    Void,
    Uncollectible,
}

public enum BillingReason
{
    SubscriptionCreate,
    SubscriptionCycle,
    SubscriptionUpdate,
    SubscriptionCancel,
    Checkout,
    Manual,
}

public enum CollectionMethod
{
    /// <summary>Tahsil charges the customer's saved card.</summary>
    ChargeAutomatically,

    /// <summary>The customer pays on the hosted invoice page by the due date.</summary>
    SendInvoice,
}

public enum InvoiceLineType
{
    Subscription,
    Usage,
    Proration,
    OneTime,
    Item,
}

public enum BalanceTransactionType
{
    Adjustment,
    InvoiceCredit,
    AppliedToInvoice,
    Carryover,
    InvoiceVoided,
}

/// <summary>A line to add: a one-time price, or a description with a unit amount (negative for a credit).</summary>
public sealed record InvoiceLineRequest
{
    /// <summary>A one-time price in the invoice's currency, by ID or lookup key.</summary>
    public string? Price { get; init; }

    /// <summary>1 when omitted.</summary>
    public decimal? Quantity { get; init; }

    public string? Description { get; init; }

    /// <summary>Minor units, for lines without a price.</summary>
    public long? UnitAmount { get; init; }

    /// <summary>For lines without a price: a tax rate by ID or key.</summary>
    public string? TaxRate { get; init; }
}

public sealed record CreateInvoiceRequest
{
    public required Guid Customer { get; init; }

    /// <summary>ISO 4217, such as <c>TRY</c>.</summary>
    [Required]
    public required string Currency { get; init; }

    public CollectionMethod CollectionMethod { get; init; } = CollectionMethod.ChargeAutomatically;

    /// <summary>For <c>send_invoice</c>: days from finalization to the due date; <c>Tahsil:Invoices:DefaultDaysUntilDue</c> when omitted.</summary>
    public int? DaysUntilDue { get; init; }

    public IReadOnlyList<InvoiceLineRequest>? Lines { get; init; }

    /// <summary>Adds the customer's pending items in the invoice's currency that belong to no subscription.</summary>
    public bool IncludePendingItems { get; init; }

    /// <summary>A coupon's ID or key for this invoice alone; redeemed when the invoice is finalized.</summary>
    public string? Coupon { get; init; }

    public string? Memo { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record AddInvoiceLinesRequest
{
    [Required]
    public required IReadOnlyList<InvoiceLineRequest> Lines { get; init; }
}

public sealed record LegalInvoiceData(string Number, DateOnly? Date = null, string? Url = null);

/// <summary>
/// JSON Merge Patch. Drafts: the collection method, days until due, memo, coupon and metadata. Finalized invoices:
/// only the legal invoice and metadata.
/// </summary>
public sealed record UpdateInvoiceRequest
{
    /// <summary>A coupon's ID or key for the draft; <c>null</c> removes it.</summary>
    public string? Coupon
    {
        get;
        init
        {
            field = value;
            HasCoupon = true;
        }
    }

    [JsonIgnore]
    public bool HasCoupon { get; private init; }

    public CollectionMethod? CollectionMethod { get; init; }

    public int? DaysUntilDue
    {
        get;
        init
        {
            field = value;
            HasDaysUntilDue = true;
        }
    }

    public string? Memo
    {
        get;
        init
        {
            field = value;
            HasMemo = true;
        }
    }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public LegalInvoiceData? LegalInvoice
    {
        get;
        init
        {
            field = value;
            HasLegalInvoice = true;
        }
    }

    [JsonIgnore]
    public bool HasDaysUntilDue { get; private init; }

    [JsonIgnore]
    public bool HasMemo { get; private init; }

    [JsonIgnore]
    public bool HasLegalInvoice { get; private init; }
}

public sealed record InvoiceLineResponse(
    Guid Id,
    InvoiceLineType Type,
    string Description,
    decimal Quantity,
    decimal? UnitAmountDecimal,
    long Amount,
    long DiscountAmount,
    long TaxAmount,
    long Total,
    Guid? PriceId,
    Guid? PendingItemId,
    Guid? TaxRateId,
    decimal? TaxPercentage,
    bool? TaxInclusive,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd);

/// <summary>The customer's billing details when the invoice was finalized.</summary>
public sealed record CustomerSnapshotResponse(
    string? Name, string? Email, CustomerType Type, string? TaxId, string? TaxOffice, CustomerAddress? Address, string? Phone, string Locale);

public sealed record InvoiceResponse
{
    public required Guid Id { get; init; }

    /// <summary>Assigned at finalization, such as <c>INV-2026-000123</c>.</summary>
    public required string? Number { get; init; }

    /// <summary>The page where the customer sees and pays the invoice (INV-24); none for drafts.</summary>
    public required Uri? HostedInvoiceUrl { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid? SubscriptionId { get; init; }

    public required InvoiceStatus Status { get; init; }

    public required BillingReason BillingReason { get; init; }

    public required CollectionMethod CollectionMethod { get; init; }

    public required int? DaysUntilDue { get; init; }

    public required string Currency { get; init; }

    public required DateTimeOffset? PeriodStart { get; init; }

    public required DateTimeOffset? PeriodEnd { get; init; }

    public required long Subtotal { get; init; }

    public required long DiscountTotal { get; init; }

    /// <summary>The discount and what it took off, by name, as the invoice shows it.</summary>
    public required IReadOnlyList<InvoiceDiscountResponse> Discounts { get; init; }

    public required long TaxTotal { get; init; }

    public required long TotalExcludingTax { get; init; }

    public required long Total { get; init; }

    /// <summary>The customer balance applied at finalization: positive when credit was spent, negative when a debt joined.</summary>
    public required long AppliedBalance { get; init; }

    public required long AmountDue { get; init; }

    public required long AmountPaid { get; init; }

    public required long AmountRemaining { get; init; }

    public required long AmountRefunded { get; init; }

    public required DateTimeOffset? DueDate { get; init; }

    public required bool AutoAdvance { get; init; }

    public required DateTimeOffset? FinalizeAt { get; init; }

    public required DateTimeOffset? NextPaymentAttemptAt { get; init; }

    public required int AttemptCount { get; init; }

    /// <summary>When dunning started: the first failure of an automatic payment for the customer, or the due date passing.</summary>
    public required DateTimeOffset? DunningStartedAt { get; init; }

    /// <summary>When dunning's final action runs if the invoice stays unpaid.</summary>
    public required DateTimeOffset? DunningDeadlineAt { get; init; }

    public required CustomerSnapshotResponse? CustomerSnapshot { get; init; }

    public required LegalInvoiceData? LegalInvoice { get; init; }

    public required string? Memo { get; init; }

    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    public required IReadOnlyList<InvoiceLineResponse> Lines { get; init; }

    public required DateTimeOffset? FinalizedAt { get; init; }

    public required DateTimeOffset? PaidAt { get; init; }

    public required DateTimeOffset? VoidedAt { get; init; }

    public required DateTimeOffset? MarkedUncollectibleAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CreateInvoiceItemRequest
{
    public required Guid Customer { get; init; }

    /// <summary>
    /// A subscription of the customer whose next invoice takes the item; while its cycle invoice is still a draft, the
    /// item joins that draft. The currency must be the subscription's.
    /// </summary>
    public Guid? Subscription { get; init; }

    [Required]
    public required string Currency { get; init; }

    /// <summary>A one-time price in the currency, by ID or lookup key; or give a description and a unit amount.</summary>
    public string? Price { get; init; }

    public string? Description { get; init; }

    /// <summary>1 when omitted.</summary>
    public decimal? Quantity { get; init; }

    /// <summary>Minor units, negative for a credit; for items without a price.</summary>
    public long? UnitAmount { get; init; }

    /// <summary>For items without a price: a tax rate by ID or key.</summary>
    public string? TaxRate { get; init; }

    public bool Discountable { get; init; } = true;
}

public sealed record InvoiceItemResponse(
    Guid Id,
    Guid CustomerId,
    Guid? SubscriptionId,
    string Currency,
    Guid? PriceId,
    string? Description,
    decimal Quantity,
    long? UnitAmount,
    Guid? TaxRateId,
    bool Discountable,
    bool Proration,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    Guid? InvoiceId,
    DateTimeOffset CreatedAt);

public sealed record CustomerBalanceResponse(string Currency, long Amount);

public sealed record CreateBalanceTransactionRequest
{
    /// <summary>Minor units: positive gives the customer credit, negative adds to what they owe.</summary>
    public required long Amount { get; init; }

    [Required]
    public required string Currency { get; init; }

    /// <summary>Why the balance changes, such as <c>Goodwill credit</c>.</summary>
    [Required]
    public required string Description { get; init; }
}

public sealed record BalanceTransactionResponse(
    Guid Id,
    Guid CustomerId,
    string Currency,
    long Amount,
    BalanceTransactionType Type,
    Guid? InvoiceId,
    string? Description,
    long BalanceAfter,
    ActorResponse Actor,
    DateTimeOffset CreatedAt);
