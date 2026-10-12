using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Checkout;

/// <summary>A line of a quote, as the first invoice will have it.</summary>
public sealed record CheckoutQuoteLine(
    Guid PriceId,
    string Description,
    decimal Quantity,
    decimal? UnitAmountDecimal,
    long Amount,
    long DiscountAmount,
    Guid? TaxRateId,
    decimal? TaxPercentage,
    bool? TaxInclusive,
    long TaxAmount,
    long Total,
    Guid? ProductId = null);

/// <summary>
/// What the customer pays (CHK-7): the first invoice computed with the invoicing pipeline (INV-4 steps 1 to 4),
/// discount included. It is computed by Tahsil, shown on the page and charged; the browser never supplies an amount
/// (CHK-33).
/// </summary>
public sealed record CheckoutQuote(
    IReadOnlyList<CheckoutQuoteLine> Lines, long Subtotal, long DiscountTotal, long TaxTotal, long TotalExcludingTax, long Total, long AmountDue)
{
    public static CheckoutQuote Empty { get; } = new([], 0, 0, 0, 0, 0, 0);

    /// <summary>The coupon the lines are discounted by.</summary>
    public InvoiceDiscount? Discount { get; init; }

    /// <summary>For subscriptions: what each renewal bills, after the first invoice or the trial (CHK-7).</summary>
    public CheckoutRecurring? Recurring { get; init; }

    /// <summary>The same lines for a customer who is, or is not, tax exempt (CHK-7).</summary>
    public CheckoutQuote ForTaxExemption(string currency, bool taxExempt) =>
        Of(Drafts(), currency, taxExempt, Discount) with
        {
            Recurring = Recurring is { } recurring ? recurring with { Period = recurring.Period.ForTaxExemption(currency, taxExempt) } : null,
        };

    /// <summary>
    /// The same lines discounted by another coupon, or by none (CHK-9). What renewals bill takes the discount only
    /// when it lasts for them all (<paramref name="recurs"/>); the page states a shorter discount's terms beside it.
    /// </summary>
    public CheckoutQuote WithDiscount(string currency, bool taxExempt, InvoiceDiscount? discount, bool recurs) =>
        Of(Drafts(), currency, taxExempt, discount) with
        {
            Recurring = Recurring is { } recurring
                ? recurring with { Period = Of(recurring.Period.Drafts(), currency, taxExempt, recurs ? discount : null) }
                : null,
        };

    /// <summary>The invoice lines of the quote, as the checkout's invoice gets them when it completes (CHK-23).</summary>
    public IReadOnlyList<InvoiceLineDraft> Drafts() =>
    [
        .. Lines.Select(line => new InvoiceLineDraft(
            InvoiceLineType.OneTime,
            line.Description,
            line.Quantity,
            line.Amount,
            line.UnitAmountDecimal,
            line.PriceId,
            TaxRateId: line.TaxRateId,
            Tax: line.TaxPercentage is { } percentage ? new TaxRateSnapshot(percentage, line.TaxInclusive == true) : null,
            ProductId: line.ProductId)),
    ];

    /// <summary>The quote of lines from prices: a draft invoice computes it, so discounts, tax and totals are an invoice's (MNY-21).</summary>
    public static CheckoutQuote Of(IReadOnlyList<InvoiceLineDraft> drafts, string currency, bool taxExempt, InvoiceDiscount? discount = null)
    {
        var invoice = Invoice.CreateDraft(
            Guid.Empty, currency, BillingReason.Checkout, CollectionMethod.ChargeAutomatically, null, null, false, Common.Metadata.Empty, DateTimeOffset.UnixEpoch, discount: discount).Value;
        invoice.AddLines(drafts, taxExempt, DateTimeOffset.UnixEpoch);
        var lines = invoice.Lines.Select(line => new CheckoutQuoteLine(
            line.PriceId ?? throw new ArgumentException("Quote lines come from prices.", nameof(drafts)),
            line.Description,
            line.Quantity,
            line.UnitAmountDecimal,
            line.Amount,
            line.DiscountAmount,
            line.TaxRateId,
            line.TaxPercentage,
            line.TaxInclusive,
            line.TaxAmount,
            line.Total,
            line.ProductId)).ToList();
        return new CheckoutQuote(lines, invoice.Subtotal, invoice.DiscountTotal, invoice.TaxTotal, invoice.TotalExcludingTax, invoice.Total, invoice.AmountDue) { Discount = discount };
    }
}

/// <summary>A period of a subscription's recurring items, every <see cref="IntervalCount"/> <see cref="Interval"/>s.</summary>
public sealed record CheckoutRecurring(Catalog.PriceInterval Interval, int IntervalCount, CheckoutQuote Period);
