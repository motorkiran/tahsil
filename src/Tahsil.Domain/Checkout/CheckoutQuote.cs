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
    long Total);

/// <summary>
/// What the customer pays (CHK-7): the first invoice computed with the invoicing pipeline (INV-4 steps 1 to 4). It is
/// computed by Tahsil, shown on the page and charged; the browser never supplies an amount (CHK-33).
/// </summary>
public sealed record CheckoutQuote(
    IReadOnlyList<CheckoutQuoteLine> Lines, long Subtotal, long DiscountTotal, long TaxTotal, long TotalExcludingTax, long Total, long AmountDue)
{
    public static CheckoutQuote Empty { get; } = new([], 0, 0, 0, 0, 0, 0);

    /// <summary>The same lines for a customer who is, or is not, tax exempt (CHK-7).</summary>
    public CheckoutQuote ForTaxExemption(bool taxExempt) => Of(Drafts(), taxExempt);

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
            Tax: line.TaxPercentage is { } percentage ? new TaxRateSnapshot(percentage, line.TaxInclusive == true) : null)),
    ];

    /// <summary>The quote of lines from prices, tax computed per line as invoices do (MNY-21).</summary>
    public static CheckoutQuote Of(IReadOnlyList<InvoiceLineDraft> drafts, bool taxExempt)
    {
        var lines = new List<CheckoutQuoteLine>();
        var amounts = new List<LineAmounts>();
        foreach (var draft in drafts)
        {
            var tax = LineArithmetic.Tax(draft.Amount, 0, draft.Tax, taxExempt);
            amounts.Add(new LineAmounts(draft.Amount, 0, tax, draft.Tax?.Inclusive == true && !taxExempt));
            lines.Add(new CheckoutQuoteLine(
                draft.PriceId ?? throw new ArgumentException("Quote lines come from prices.", nameof(drafts)),
                draft.Description,
                draft.Quantity,
                draft.UnitAmountDecimal,
                draft.Amount,
                0,
                draft.TaxRateId,
                draft.Tax?.Percentage,
                draft.Tax?.Inclusive,
                tax.Tax,
                tax.Total));
        }

        var totals = InvoiceTotals.Of(amounts);
        return new CheckoutQuote(lines, totals.Subtotal, totals.DiscountTotal, totals.TaxTotal, totals.TotalExcludingTax, totals.Total, Math.Max(totals.Total, 0));
    }
}
