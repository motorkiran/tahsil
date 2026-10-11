using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Subscriptions;

/// <summary>The lines a subscription bills (INV-1 to INV-3), described in the customer's language.</summary>
public sealed class SubscriptionLines(ITahsilDbContext context, IOptions<BillingOptions> billing)
{
    /// <summary>
    /// The licensed items for a period, billed in advance; a first period that ends at a later anchor bills its share
    /// of the full period before the anchor (SUB-4, MNY-29). Items that come to nothing are omitted.
    /// </summary>
    public async Task<List<InvoiceLineDraft>> LicensedAsync(Subscription subscription, BillingPeriod period, string locale, CancellationToken cancellationToken)
    {
        var priceIds = subscription.Items.Select(item => item.PriceId).ToList();
        var prices = await context.Prices.AsNoTracking().Where(price => priceIds.Contains(price.Id)).ToDictionaryAsync(price => price.Id, cancellationToken);
        var productIds = prices.Values.Select(price => price.ProductId).Distinct().ToList();
        var products = await context.Products.AsNoTracking().Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id, cancellationToken);
        var rateIds = prices.Values.Where(price => price.TaxRateId is not null).Select(price => price.TaxRateId!.Value).Distinct().ToList();
        var rates = await context.TaxRates.AsNoTracking().Where(rate => rateIds.Contains(rate.Id)).ToDictionaryAsync(rate => rate.Id, cancellationToken);

        var full = period.End <= subscription.BillingCycleAnchor
            ? BillingPeriods.Period(subscription.BillingCycleAnchor, subscription.Interval, subscription.IntervalCount, -1, billing.Value.Zone)
            : period;
        var fraction = full == period ? 1m : Proration.Fraction(full, period.Start);
        var lines = new List<InvoiceLineDraft>();
        foreach (var item in subscription.Items)
        {
            var price = prices[item.PriceId];
            if (price.UsageType == UsageType.Metered || item.Quantity is not (> 0 and var quantity))
            {
                continue;
            }

            var amount = Proration.Amounts(0, PriceAmounts.For(price, quantity), fraction).Charge;
            if (amount == 0)
            {
                continue;
            }

            var rate = price.TaxRateId is { } rateId ? rates[rateId] : null;
            lines.Add(new InvoiceLineDraft(
                InvoiceLineType.Subscription,
                InvoiceLines.Describe(products[price.ProductId], price, quantity, locale),
                quantity,
                amount,
                UnitAmountDecimal: InvoiceLines.UnitAmountOf(price),
                PriceId: price.Id,
                TaxRateId: rate?.Id,
                Tax: rate is null ? null : new TaxRateSnapshot(rate.Percentage, rate.IsInclusive),
                PeriodStart: period.Start,
                PeriodEnd: period.End));
        }

        return lines;
    }
}
