namespace Tahsil.Domain.Money;

/// <summary>A line's taxable amount, tax, total and net (MNY-12 to MNY-17).</summary>
public readonly record struct LineTax(long Taxable, long Tax, long Total, long Net);

/// <summary>The tax rate of a line, as invoices snapshot it.</summary>
public sealed record TaxRateSnapshot(decimal Percentage, bool Inclusive);

/// <summary>Tax and discounts per line; invoice totals are sums of these rounded values.</summary>
public static class LineArithmetic
{
    /// <summary>
    /// The tax of a line after its discount, from its own taxable amount: exclusive taxes are added, inclusive taxes are
    /// inside the amount. Exempt customers and untaxed lines pay no tax and keep the amounts as priced.
    /// </summary>
    public static LineTax Tax(long amount, long discount, TaxRateSnapshot? rate, bool exempt)
    {
        var taxable = checked(amount - discount);
        if (rate is null || exempt)
        {
            return new LineTax(taxable, 0, taxable, taxable);
        }

        if (rate.Inclusive)
        {
            var inside = MoneyMath.Round(taxable * rate.Percentage / (100 + rate.Percentage));
            return new LineTax(taxable, inside, taxable, checked(taxable - inside));
        }

        var added = MoneyMath.Round(taxable * rate.Percentage / 100);
        return new LineTax(taxable, added, checked(taxable + added), taxable);
    }

    /// <summary>A percentage discount of one line, with the line's sign (MNY-18).</summary>
    public static long PercentageDiscount(long amount, decimal percentOff) => MoneyMath.Round(amount * percentOff / 100);

    /// <summary>
    /// A fixed discount over the lines (MNY-19, MNY-20): only positive lines take part, the discount is capped at their sum,
    /// and it is split in proportion to their amounts. Other lines get 0.
    /// </summary>
    public static long[] FixedDiscount(long amountOff, IReadOnlyList<long> lineAmounts)
    {
        var positive = lineAmounts.Select(amount => Math.Max(amount, 0)).ToList();
        var applied = Math.Min(amountOff, positive.Sum());
        return applied <= 0 ? new long[lineAmounts.Count] : MoneyMath.Allocate(applied, positive);
    }
}

/// <summary>The money of one invoice line, after discount and tax.</summary>
public sealed record LineAmounts(long Amount, long Discount, LineTax Tax, bool InclusiveTax);

/// <summary>An invoice's totals, as sums of its lines' rounded values (MNY-21).</summary>
public sealed record InvoiceTotals(long Subtotal, long DiscountTotal, long TaxTotal, long Total, long TotalExcludingTax)
{
    public static InvoiceTotals Of(IEnumerable<LineAmounts> lines)
    {
        long subtotal = 0, discount = 0, tax = 0, total = 0, excluding = 0;
        foreach (var line in lines)
        {
            subtotal = checked(subtotal + line.Amount);
            discount = checked(discount + line.Discount);
            tax = checked(tax + line.Tax.Tax);
            total = checked(total + line.Tax.Total);
            excluding = checked(excluding + line.Tax.Taxable - (line.InclusiveTax ? line.Tax.Tax : 0));
        }

        return new InvoiceTotals(subtotal, discount, tax, total, excluding);
    }
}
