using Tahsil.Domain.Catalog;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Subscriptions;

/// <summary>An item of a subscription with the tax rate of its price, for the subscription's recurring revenue.</summary>
public sealed record RecurringItem(Price Price, int Quantity, TaxRateSnapshot? Tax);

/// <summary>The coupon of a subscription's active discount: a percentage or an amount, of every product or of some.</summary>
public sealed record RecurringDiscount(decimal? PercentOff, long? AmountOff, string? Currency, IReadOnlyList<Guid>? ProductIds);

/// <summary>
/// Monthly recurring revenue (ADM-5): what a subscription's licensed items bill per period after its discount and
/// without tax, brought to a month. Metered items are left out. An operational figure, not an accounting one.
/// </summary>
public static class RecurringRevenue
{
    /// <summary>The subscription's revenue per month, in minor units of <paramref name="currency"/>, not rounded.</summary>
    public static decimal Monthly(string currency, IReadOnlyList<RecurringItem> items, RecurringDiscount? discount)
    {
        var licensed = items.Where(item => item.Price is { UsageType: not UsageType.Metered, Interval: not null }).ToList();
        var amounts = licensed.Select(item => PriceAmounts.For(item.Price, item.Quantity)).ToList();
        var discounts = new long[licensed.Count];
        if (discount is not null)
        {
            var discounted = Enumerable.Range(0, licensed.Count)
                .Where(index => discount.ProductIds is null || discount.ProductIds.Contains(licensed[index].Price.ProductId))
                .ToList();
            var shares = discount switch
            {
                { PercentOff: { } percent } => [.. discounted.Select(index => LineArithmetic.PercentageDiscount(amounts[index], percent))],
                { AmountOff: { } amount } when discount.Currency == currency => LineArithmetic.FixedDiscount(amount, [.. discounted.Select(index => amounts[index])]),
                _ => new long[discounted.Count],
            };
            foreach (var (position, index) in discounted.Index())
            {
                discounts[index] = shares[position];
            }
        }

        var monthly = 0m;
        foreach (var (index, item) in licensed.Index())
        {
            decimal net = amounts[index] - discounts[index];
            if (item.Tax is { Inclusive: true } included)
            {
                net /= 1 + (included.Percentage / 100);
            }

            // Multiplied before it is divided, so that whole amounts stay whole.
            var (periods, months) = PerMonths(item.Price.Interval!.Value, item.Price.IntervalCount ?? 1);
            monthly += net * periods / months;
        }

        return monthly;
    }

    /// <summary>How many of a price's periods fall in how many months: 12 months a year, with 52 weeks or 365 days.</summary>
    private static (int Periods, int Months) PerMonths(PriceInterval interval, int intervalCount) => interval switch
    {
        PriceInterval.Year => (1, 12 * intervalCount),
        PriceInterval.Week => (52, 12 * intervalCount),
        PriceInterval.Day => (365, 12 * intervalCount),
        _ => (1, intervalCount),
    };
}
