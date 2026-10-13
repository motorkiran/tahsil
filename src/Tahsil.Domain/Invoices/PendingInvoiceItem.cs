using Tahsil.Domain.Common;

namespace Tahsil.Domain.Invoices;

/// <summary>
/// A charge or credit waiting for an invoice: a subscription's joins its next invoice, others the next manual invoice
/// that asks for them. Once a finalized invoice includes it, it never changes.
/// </summary>
public sealed class PendingInvoiceItem : AggregateRoot
{
    public const int DescriptionMaxLength = 500;

    private PendingInvoiceItem()
    {
    }

    public Guid Id { get; private init; }

    public Guid CustomerId { get; private init; }

    public Guid? SubscriptionId { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    /// <summary>A one-time price, or <see langword="null"/> with <see cref="UnitAmount"/>.</summary>
    public Guid? PriceId { get; private init; }

    public string? Description { get; private init; }

    public decimal Quantity { get; private init; }

    /// <summary>Minor units; negative for credits.</summary>
    public long? UnitAmount { get; private init; }

    public Guid? TaxRateId { get; private init; }

    public bool IsDiscountable { get; private init; }

    public bool IsProration { get; private init; }

    public DateTimeOffset? PeriodStart { get; private init; }

    public DateTimeOffset? PeriodEnd { get; private init; }

    public Guid? InvoiceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsPending => InvoiceId is null;

    public static Result<PendingInvoiceItem> Create(
        Guid customerId,
        string currency,
        Guid? priceId,
        string? description,
        decimal quantity,
        long? unitAmount,
        Guid? taxRateId,
        bool isDiscountable,
        DateTimeOffset now,
        Guid? subscriptionId = null)
    {
        if (quantity <= 0 || decimal.Round(quantity, 8) != quantity)
        {
            return InvoiceErrors.InvalidLine("quantity", "The quantity is positive, with at most eight decimals.");
        }

        if (priceId is null && (unitAmount is null || !Text.FitsRequired(description, DescriptionMaxLength)))
        {
            return InvoiceErrors.InvalidLine("description", "An item without a price needs a description of at most 500 characters and a unit amount.");
        }

        if (priceId is not null && (unitAmount is not null || !Text.FitsOptional(description, DescriptionMaxLength)))
        {
            return InvoiceErrors.InvalidLine("unitAmount", "An item with a price takes its amount from the price; its description is at most 500 characters.");
        }

        return new PendingInvoiceItem
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            SubscriptionId = subscriptionId,
            Currency = currency,
            PriceId = priceId,
            Description = Text.Normalize(description),
            Quantity = quantity,
            UnitAmount = unitAmount,
            TaxRateId = taxRateId,
            IsDiscountable = isDiscountable,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// The usage of a metered item that left its subscription, waiting for the subscription's next invoice (SUB-25):
    /// its amount for the usage until then, described like a usage line (INV-2).
    /// </summary>
    public static PendingInvoiceItem ForUsage(
        Guid customerId, Guid subscriptionId, string currency, string description, long amount, Guid? taxRateId, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            SubscriptionId = subscriptionId,
            Currency = currency,
            Description = description,
            Quantity = 1,
            UnitAmount = amount,
            TaxRateId = taxRateId,
            IsDiscountable = true,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>
    /// A proration line waiting for its subscription's next invoice (SUB-20): its amount for the remaining period, with
    /// the price's tax rate, described like the line it becomes (INV-2).
    /// </summary>
    public static PendingInvoiceItem ForProration(
        Guid customerId, Guid subscriptionId, string currency, string description, long amount, Guid? taxRateId, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            SubscriptionId = subscriptionId,
            Currency = currency,
            Description = description,
            Quantity = 1,
            UnitAmount = amount,
            TaxRateId = taxRateId,
            IsDiscountable = true,
            IsProration = true,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public void IncludeIn(Guid invoiceId, DateTimeOffset now)
    {
        if (InvoiceId is not null)
        {
            throw new InvalidOperationException($"The pending item {Id} is already on invoice {InvoiceId}.");
        }

        InvoiceId = invoiceId;
        UpdatedAt = now;
    }

    /// <summary>Frees the item when the draft that included it drops it or is deleted.</summary>
    public void Release(DateTimeOffset now)
    {
        InvoiceId = null;
        UpdatedAt = now;
    }
}
