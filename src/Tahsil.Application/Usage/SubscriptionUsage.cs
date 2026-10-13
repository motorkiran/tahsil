using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Usage;

/// <summary>
/// The usage a subscription's metered items bill, in arrears (USG-8 to USG-12). Each item bills what its customer used
/// on its meter since the item last invoiced usage, or since it started metering: when it joined the subscription, or
/// when the trial ended, since a trial's usage is free.
/// </summary>
public sealed class SubscriptionUsage(ITahsilDbContext context, IUsageStore store, BillingClock clock)
{
    /// <summary>The usage lines of <paramref name="items"/> up to <paramref name="until"/> as things stand, without invoicing them.</summary>
    public Task<List<InvoiceLineDraft>> PreviewAsync(
        Subscription subscription, IReadOnlyCollection<SubscriptionItem> items, DateTimeOffset until, string locale, CancellationToken cancellationToken) =>
        LinesAsync(subscription, items, until, locale, invoicing: false, cancellationToken);

    /// <summary>
    /// The usage lines of <paramref name="items"/> up to <paramref name="until"/>, for an invoice that is finalized in
    /// this transaction: the items are locked against usage reporting first, and their usage before
    /// <paramref name="until"/> is closed to new events (USG-11, USG-12).
    /// </summary>
    public Task<List<InvoiceLineDraft>> InvoiceAsync(
        Subscription subscription, IReadOnlyCollection<SubscriptionItem> items, DateTimeOffset until, string locale, CancellationToken cancellationToken) =>
        LinesAsync(subscription, items, until, locale, invoicing: true, cancellationToken);

    /// <summary>
    /// Brings the usage lines of a cycle invoice up to date as it is finalized (USG-11): it bills its subscription's
    /// usage before its period, with every event reported since the draft was made.
    /// </summary>
    public async Task RefreshAsync(Invoice invoice, bool taxExempt, string locale, CancellationToken cancellationToken)
    {
        if (invoice is not { BillingReason: BillingReason.SubscriptionCycle, SubscriptionId: { } subscriptionId, PeriodStart: { } until })
        {
            return;
        }

        // Locked before the subscription is read, so that its items are as they are now.
        var cursors = await store.LockItemsAsync(subscriptionId, cancellationToken);
        var subscription = await context.Subscriptions.Include(row => row.Items).SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        var lines = await LinesAsync(subscription, subscription.Items, until, locale, cursors, cancellationToken);
        invoice.ReplaceUsageLines(lines, taxExempt, clock.GetUtcNow());
    }

    /// <summary>
    /// What each metered item used in the subscription's current period so far, and what that would bill now (USG §3).
    /// During a trial, the usage is shown and comes to nothing. None for a subscription that is over.
    /// </summary>
    public async Task<List<CurrentUsage>> CurrentAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var priceIds = subscription.Items.Select(item => item.PriceId).ToList();
        var prices = subscription.IsTerminal
            ? []
            : await context.Prices.AsNoTracking().Where(price => priceIds.Contains(price.Id) && price.MeterId != null).ToDictionaryAsync(price => price.Id, cancellationToken);
        if (prices.Count == 0)
        {
            return [];
        }

        var meterIds = prices.Values.Select(price => price.MeterId!.Value).Distinct().ToList();
        var meters = await context.Meters.AsNoTracking().Where(meter => meterIds.Contains(meter.Id)).ToDictionaryAsync(meter => meter.Id, cancellationToken);
        var productIds = prices.Values.Select(price => price.ProductId).Distinct().ToList();
        var products = await context.Products.AsNoTracking().Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id, cancellationToken);
        var until = subscription.CurrentPeriodEnd;
        var inTrial = subscription.TrialEnd is { } trialEnd && until <= trialEnd;

        var current = new List<CurrentUsage>();
        foreach (var item in subscription.Items.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id))
        {
            if (!prices.TryGetValue(item.PriceId, out var price))
            {
                continue;
            }

            // What came before the period is on its invoice, or waits for the next one.
            var from = new[] { item.UsageBilledUntil ?? item.CreatedAt, subscription.CurrentPeriodStart }.Max();
            if (from >= until)
            {
                continue;
            }

            var meter = meters[price.MeterId!.Value];
            var quantity = await store.AggregateAsync(subscription.CustomerId, meter, from, until, cancellationToken);
            current.Add(new CurrentUsage(item, price, products[price.ProductId], meter, from, until, quantity, inTrial || quantity == 0 ? 0 : PriceAmounts.For(price, quantity)));
        }

        return current;
    }

    private async Task<List<InvoiceLineDraft>> LinesAsync(
        Subscription subscription, IReadOnlyCollection<SubscriptionItem> items, DateTimeOffset until, string locale, bool invoicing, CancellationToken cancellationToken)
    {
        if (items.Count == 0 || !await AnyMeteredAsync(items, cancellationToken))
        {
            return [];
        }

        return await LinesAsync(subscription, items, until, locale, invoicing ? await store.LockItemsAsync(subscription.Id, cancellationToken) : null, cancellationToken);
    }

    /// <summary>With <paramref name="cursors"/>, read under the items' lock, the lines are invoiced and the items' usage is closed.</summary>
    private async Task<List<InvoiceLineDraft>> LinesAsync(
        Subscription subscription,
        IReadOnlyCollection<SubscriptionItem> items,
        DateTimeOffset until,
        string locale,
        IReadOnlyList<UsageCursor>? cursors,
        CancellationToken cancellationToken)
    {
        var priceIds = items.Select(item => item.PriceId).ToList();
        var prices = await context.Prices.AsNoTracking().Where(price => priceIds.Contains(price.Id) && price.MeterId != null).ToDictionaryAsync(price => price.Id, cancellationToken);
        if (prices.Count == 0)
        {
            return [];
        }

        var meterIds = prices.Values.Select(price => price.MeterId!.Value).Distinct().ToList();
        var meters = await context.Meters.AsNoTracking().Where(meter => meterIds.Contains(meter.Id)).ToDictionaryAsync(meter => meter.Id, cancellationToken);
        var rateIds = prices.Values.Where(price => price.TaxRateId is not null).Select(price => price.TaxRateId!.Value).Distinct().ToList();
        var rates = await context.TaxRates.AsNoTracking().Where(rate => rateIds.Contains(rate.Id)).ToDictionaryAsync(rate => rate.Id, cancellationToken);
        var billed = cursors?.ToDictionary(cursor => cursor.ItemId, cursor => cursor.BilledUntil);
        var culture = CultureInfo.GetCultureInfo(locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr-TR" : "en-US");
        var now = clock.GetUtcNow();

        var lines = new List<InvoiceLineDraft>();
        foreach (var item in items)
        {
            if (!prices.TryGetValue(item.PriceId, out var price))
            {
                continue;
            }

            DateTimeOffset? billedUntil = item.UsageBilledUntil;
            if (billed is not null && !billed.TryGetValue(item.Id, out billedUntil))
            {
                // The item left the subscription meanwhile, and its usage was billed as it left (SUB-25).
                continue;
            }

            var from = billedUntil ?? item.CreatedAt;
            if (subscription.TrialEnd is { } trialEnd && from < trialEnd)
            {
                from = trialEnd;
            }

            if (from >= until)
            {
                continue;
            }

            if (billed is not null)
            {
                item.CloseUsage(until, now);
            }

            var meter = meters[price.MeterId!.Value];
            var quantity = await store.AggregateAsync(subscription.CustomerId, meter, from, until, cancellationToken);
            var amount = quantity == 0 ? 0 : PriceAmounts.For(price, quantity);
            if (amount == 0)
            {
                continue;
            }

            var rate = price.TaxRateId is { } rateId ? rates[rateId] : null;
            lines.Add(new InvoiceLineDraft(
                InvoiceLineType.Usage,
                $"{meter.Name}: {quantity.ToString("#,0.########", culture)}",
                quantity,
                amount,
                UnitAmountDecimal: InvoiceLines.UnitAmountOf(price),
                PriceId: price.Id,
                ProductId: price.ProductId,
                TaxRateId: rate?.Id,
                Tax: rate is null ? null : new TaxRateSnapshot(rate.Percentage, rate.IsInclusive),
                PeriodStart: from,
                PeriodEnd: until));
        }

        return lines;
    }

    private async Task<bool> AnyMeteredAsync(IReadOnlyCollection<SubscriptionItem> items, CancellationToken cancellationToken)
    {
        var priceIds = items.Select(item => item.PriceId).ToList();
        return await context.Prices.AnyAsync(price => priceIds.Contains(price.Id) && price.MeterId != null, cancellationToken);
    }
}

/// <summary>A metered item's usage over [<paramref name="From"/>, <paramref name="Until"/>) and the amount it comes to.</summary>
public sealed record CurrentUsage(
    SubscriptionItem Item, Domain.Catalog.Price Price, Domain.Catalog.Product Product, Domain.Catalog.Meter Meter, DateTimeOffset From, DateTimeOffset Until, decimal Quantity, long Amount);

/// <summary>
/// USG-7: a customer's usage on a meter is billed by one subscription item. Items of one subscription have different
/// meters, and no other subscription of the customer that is not over has an item with one of them.
/// </summary>
public sealed class MeterRule(ITahsilDbContext context)
{
    public async Task<Error?> CheckAsync(Guid? customerId, IReadOnlyList<SubscriptionItemSpec> items, Guid? subscriptionId, CancellationToken cancellationToken)
    {
        var meters = items.Where(item => item.Price.MeterId is not null).Select(item => item.Price.MeterId!.Value).ToList();
        if (meters.Count == 0)
        {
            return null;
        }

        return meters.Distinct().Count() != meters.Count || (customerId is { } customer && await context.Subscriptions.AnyAsync(
                subscription => subscription.CustomerId == customer && subscription.Id != subscriptionId
                    && subscription.Status != SubscriptionStatus.Canceled && subscription.Status != SubscriptionStatus.IncompleteExpired
                    && subscription.Items.Any(item => context.Prices.Any(price => price.Id == item.PriceId && price.MeterId != null && meters.Contains(price.MeterId.Value))),
                cancellationToken))
            ? SubscriptionErrors.MeterConflict
            : null;
    }
}
