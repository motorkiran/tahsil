using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Subscriptions;

/// <summary>The lines a subscription bills (INV-1 to INV-3, MNY-29 to MNY-31), described in the customer's language.</summary>
public sealed class SubscriptionLines(ITahsilDbContext context, IOptions<BillingOptions> billing)
{
    /// <summary>
    /// The licensed items for a period, billed in advance; a first period that ends at a later anchor bills its share
    /// of the full period before the anchor (SUB-4, MNY-29). Items that come to nothing are omitted.
    /// </summary>
    public async Task<List<InvoiceLineDraft>> LicensedAsync(Subscription subscription, BillingPeriod period, string locale, CancellationToken cancellationToken)
    {
        var full = period.End <= subscription.BillingCycleAnchor
            ? BillingPeriods.Period(subscription.BillingCycleAnchor, subscription.Interval, subscription.IntervalCount, -1, billing.Value.Zone)
            : period;
        return await LicensedAsync(await PricedAsync(subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken), period, full, locale, cancellationToken);
    }

    /// <summary>Items with their prices and products, in the given order.</summary>
    public async Task<List<SubscriptionItemSpec>> PricedAsync(IEnumerable<(Guid PriceId, int? Quantity)> items, CancellationToken cancellationToken)
    {
        var list = items.ToList();
        var priceIds = list.Select(item => item.PriceId).ToList();
        var prices = await context.Prices.AsNoTracking().Where(price => priceIds.Contains(price.Id)).ToDictionaryAsync(price => price.Id, cancellationToken);
        var productIds = prices.Values.Select(price => price.ProductId).Distinct().ToList();
        var products = await context.Products.AsNoTracking().Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id, cancellationToken);
        return [.. list.Select(item => new SubscriptionItemSpec(prices[item.PriceId], products[prices[item.PriceId].ProductId], item.Quantity))];
    }

    /// <summary>The licensed items' lines for <paramref name="period"/>, a share of <paramref name="full"/>.</summary>
    public async Task<List<InvoiceLineDraft>> LicensedAsync(
        IReadOnlyList<SubscriptionItemSpec> items, BillingPeriod period, BillingPeriod full, string locale, CancellationToken cancellationToken)
    {
        var rates = await RatesAsync(items, cancellationToken);
        var fraction = full == period ? 1m : Proration.Fraction(full, period.Start);
        var lines = new List<InvoiceLineDraft>();
        foreach (var (price, product, itemQuantity) in items)
        {
            if (price.UsageType == UsageType.Metered || itemQuantity is not (> 0 and var quantity))
            {
                continue;
            }

            var amount = Proration.Amounts(0, PriceAmounts.For(price, quantity), fraction).Charge;
            if (amount != 0)
            {
                lines.Add(Line(InvoiceLineType.Subscription, InvoiceLines.Describe(product, price, quantity, locale), price, quantity, amount, rates, period, InvoiceLines.UnitAmountOf(price)));
            }
        }

        return lines;
    }

    /// <summary>
    /// The proration lines of a change at <paramref name="at"/> within <paramref name="period"/> (MNY-29, MNY-30): each
    /// licensed item that changed is credited as it was and charged as it becomes, for the remaining share of the
    /// period; with <paramref name="creditsOnly"/>, only the credits. Unchanged and metered items have none (MNY-31).
    /// </summary>
    public async Task<List<InvoiceLineDraft>> ProrationAsync(
        IReadOnlyList<SubscriptionItemSpec> before,
        IReadOnlyList<SubscriptionItemSpec> after,
        BillingPeriod period,
        DateTimeOffset at,
        bool creditsOnly,
        string locale,
        CancellationToken cancellationToken)
    {
        var rates = await RatesAsync([.. before, .. after], cancellationToken);
        var fraction = Proration.Fraction(period, at);
        var remaining = new BillingPeriod(at < period.Start ? period.Start : at, period.End);
        var culture = Culture(locale);
        var date = TimeZoneInfo.ConvertTime(at, billing.Value.Zone).ToString("d MMM yyyy", culture);
        var turkish = culture.Name == "tr-TR";
        var credits = new List<InvoiceLineDraft>();
        var charges = new List<InvoiceLineDraft>();
        foreach (var priceId in before.Select(item => item.Price.Id).Concat(after.Select(item => item.Price.Id)).Distinct())
        {
            var old = Licensed(before, priceId);
            var @new = creditsOnly ? null : Licensed(after, priceId);
            if (old is not null && @new is not null && old.Quantity == @new.Quantity)
            {
                continue;
            }

            var amounts = Proration.Amounts(Amount(old), Amount(@new), fraction);
            if (old is { Quantity: { } oldQuantity } && amounts.Credit != 0)
            {
                var name = $"{PriceDisplay.Name(old.Product.Name, old.Price.Name)} × {oldQuantity.ToString(culture)}";
                credits.Add(Line(
                    InvoiceLineType.Proration, turkish ? $"{date} sonrası kullanılmayan süre: {name}" : $"Unused time on {name} after {date}", old.Price, oldQuantity, amounts.Credit, rates, remaining));
            }

            if (@new is { Quantity: { } newQuantity } && amounts.Charge != 0)
            {
                var name = $"{PriceDisplay.Name(@new.Product.Name, @new.Price.Name)} × {newQuantity.ToString(culture)}";
                charges.Add(Line(
                    InvoiceLineType.Proration, turkish ? $"{date} sonrası kalan süre: {name}" : $"Remaining time on {name} after {date}", @new.Price, newQuantity, amounts.Charge, rates, remaining));
            }
        }

        return [.. credits, .. charges];
    }

    private static SubscriptionItemSpec? Licensed(IReadOnlyList<SubscriptionItemSpec> items, Guid priceId) =>
        items.FirstOrDefault(item => item.Price.Id == priceId && item.Price.UsageType != UsageType.Metered && item.Quantity > 0);

    private static long Amount(SubscriptionItemSpec? item) => item is { Quantity: { } quantity } ? PriceAmounts.For(item.Price, quantity) : 0;

    private static CultureInfo Culture(string locale) => CultureInfo.GetCultureInfo(locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr-TR" : "en-US");

    private static InvoiceLineDraft Line(
        InvoiceLineType type, string description, Price price, int quantity, long amount, Dictionary<Guid, TaxRate> rates, BillingPeriod period, decimal? unitAmount = null)
    {
        var rate = price.TaxRateId is { } rateId ? rates[rateId] : null;
        return new InvoiceLineDraft(
            type,
            description,
            quantity,
            amount,
            UnitAmountDecimal: unitAmount,
            PriceId: price.Id,
            ProductId: price.ProductId,
            TaxRateId: rate?.Id,
            Tax: rate is null ? null : new TaxRateSnapshot(rate.Percentage, rate.IsInclusive),
            PeriodStart: period.Start,
            PeriodEnd: period.End);
    }

    private async Task<Dictionary<Guid, TaxRate>> RatesAsync(IEnumerable<SubscriptionItemSpec> items, CancellationToken cancellationToken)
    {
        var rateIds = items.Where(item => item.Price.TaxRateId is not null).Select(item => item.Price.TaxRateId!.Value).Distinct().ToList();
        return await context.TaxRates.AsNoTracking().Where(rate => rateIds.Contains(rate.Id)).ToDictionaryAsync(rate => rate.Id, cancellationToken);
    }
}
