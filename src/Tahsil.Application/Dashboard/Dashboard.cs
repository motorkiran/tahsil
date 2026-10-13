using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Payments;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Dashboard;

/// <summary>The dashboard's figures, per currency: currencies are never added together (ADM-5).</summary>
public sealed record DashboardFigures(DateTimeOffset ComputedAt, IReadOnlyList<CurrencyFigures> Currencies);

/// <summary>
/// A currency's figures. Subscriptions count as active while they are active or past due. "This month" is the month
/// of the billing time zone; revenue, failed payments and dunning look at the last 30 days.
/// </summary>
/// <param name="Currency">The currency of every amount here.</param>
/// <param name="Mrr">Monthly recurring revenue (ADM-5), in minor units.</param>
/// <param name="Active">Subscriptions that are active or past due.</param>
/// <param name="Trialing">Subscriptions in their trial.</param>
/// <param name="NewThisMonth">Subscriptions created this month that got past their first payment, or need none yet.</param>
/// <param name="ChurnedThisMonth">Subscriptions that ended this month.</param>
/// <param name="Revenue">Payments that succeeded, less refunds, in minor units.</param>
/// <param name="FailedPayments">Payments that failed.</param>
/// <param name="DunningStarted">Invoices whose collection failed or whose due date passed.</param>
/// <param name="Recovered">Those of <paramref name="DunningStarted"/> that were paid since.</param>
/// <param name="AwaitingReconciliation">Payments whose outcome the provider has not told yet.</param>
public sealed record CurrencyFigures(
    string Currency,
    long Mrr,
    int Active,
    int Trialing,
    int NewThisMonth,
    int ChurnedThisMonth,
    long Revenue,
    int FailedPayments,
    int DunningStarted,
    int Recovered,
    int AwaitingReconciliation)
{
    /// <summary>The share of invoices in dunning that were paid; none when no invoice entered dunning.</summary>
    public decimal? RecoveryRate => DunningStarted == 0 ? null : (decimal)Recovered / DunningStarted;
}

/// <summary>Keeps the figures an instance computed last (ADM-6).</summary>
public sealed class DashboardCache
{
    private volatile DashboardFigures? _figures;

    public DashboardFigures? Figures
    {
        get => _figures;
        set => _figures = value;
    }
}

/// <summary>
/// The admin dashboard's figures (ADM §3), computed from Tahsil's tables on request and kept for five minutes
/// (ADM-6). They are operational figures, not accounting reports.
/// </summary>
public sealed class GetDashboardHandler(ITahsilDbContext context, IAuthorizer authorizer, DashboardCache cache, IOptions<BillingOptions> billing, BillingClock clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private static readonly string[] Needed = [Contracts.TahsilPermissions.SubscriptionsRead, Contracts.TahsilPermissions.InvoicesRead, Contracts.TahsilPermissions.PaymentsRead];

    public async Task<Result<DashboardFigures>> HandleAsync(CancellationToken cancellationToken)
    {
        foreach (var permission in Needed)
        {
            if (authorizer.Require(permission) is { IsFailure: true } denied)
            {
                return denied.Error;
            }
        }

        var now = clock.GetUtcNow();
        if (cache.Figures is { } kept && now >= kept.ComputedAt && now - kept.ComputedAt < Lifetime)
        {
            return kept;
        }

        var figures = await ComputeAsync(now, cancellationToken);
        cache.Figures = figures;
        return figures;
    }

    private async Task<DashboardFigures> ComputeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var zone = billing.Value.Zone;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var firstOfMonth = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var month = new DateTimeOffset(firstOfMonth, zone.GetUtcOffset(firstOfMonth)).ToUniversalTime();
        var since = now - Window;

        var byStatus = await context.Subscriptions.AsNoTracking()
            .GroupBy(subscription => new { subscription.Currency, subscription.Status })
            .Select(group => new { group.Key.Currency, group.Key.Status, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var created = await context.Subscriptions.AsNoTracking()
            .Where(subscription => subscription.CreatedAt >= month && subscription.Status != SubscriptionStatus.Incomplete && subscription.Status != SubscriptionStatus.IncompleteExpired)
            .GroupBy(subscription => subscription.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Currency, row => row.Count, cancellationToken);
        var churned = await context.Subscriptions.AsNoTracking()
            .Where(subscription => subscription.Status == SubscriptionStatus.Canceled && subscription.EndedAt >= month)
            .GroupBy(subscription => subscription.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Currency, row => row.Count, cancellationToken);

        var paid = await context.Payments.AsNoTracking()
            .Where(payment => payment.Status == PaymentStatus.Succeeded && payment.SucceededAt >= since)
            .GroupBy(payment => payment.Currency)
            .Select(group => new { Currency = group.Key, Amount = group.Sum(payment => payment.Amount) })
            .ToDictionaryAsync(row => row.Currency, row => row.Amount, cancellationToken);
        var refunded = await context.Refunds.AsNoTracking()
            .Where(refund => refund.Status == RefundStatus.Succeeded && refund.SucceededAt >= since)
            .Join(context.Payments, refund => refund.PaymentId, payment => payment.Id, (refund, payment) => new { payment.Currency, refund.Amount })
            .GroupBy(row => row.Currency)
            .Select(group => new { Currency = group.Key, Amount = group.Sum(row => row.Amount) })
            .ToDictionaryAsync(row => row.Currency, row => row.Amount, cancellationToken);
        var failed = await context.Payments.AsNoTracking()
            .Where(payment => payment.Status == PaymentStatus.Failed && payment.FailedAt >= since)
            .GroupBy(payment => payment.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Currency, row => row.Count, cancellationToken);
        var unsettled = await context.Payments.AsNoTracking()
            .Where(payment => payment.Status == PaymentStatus.Processing)
            .GroupBy(payment => payment.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Currency, row => row.Count, cancellationToken);
        var dunning = await context.Invoices.AsNoTracking()
            .Where(invoice => invoice.DunningStartedAt >= since)
            .GroupBy(invoice => new { invoice.Currency, Paid = invoice.Status == InvoiceStatus.Paid })
            .Select(group => new { group.Key.Currency, group.Key.Paid, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var mrr = await MrrAsync(cancellationToken);
        var currencies = byStatus.Select(row => row.Currency)
            .Concat(paid.Keys).Concat(refunded.Keys).Concat(failed.Keys).Concat(unsettled.Keys).Concat(dunning.Select(row => row.Currency))
            .Distinct().Order(StringComparer.Ordinal);
        return new DashboardFigures(now, [.. currencies.Select(currency => new CurrencyFigures(
            currency,
            MoneyMath.Round(mrr.GetValueOrDefault(currency)),
            byStatus.Where(row => row.Currency == currency && row.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue).Sum(row => row.Count),
            byStatus.Where(row => row.Currency == currency && row.Status == SubscriptionStatus.Trialing).Sum(row => row.Count),
            created.GetValueOrDefault(currency),
            churned.GetValueOrDefault(currency),
            paid.GetValueOrDefault(currency) - refunded.GetValueOrDefault(currency),
            failed.GetValueOrDefault(currency),
            dunning.Where(row => row.Currency == currency).Sum(row => row.Count),
            dunning.Where(row => row.Currency == currency && row.Paid).Sum(row => row.Count),
            unsettled.GetValueOrDefault(currency)))]);
    }

    /// <summary>
    /// ADM-5 per currency. Subscriptions without a discount are counted by price and quantity in the database; those
    /// with one are computed one by one, since a discount spreads over a subscription's items.
    /// </summary>
    private async Task<Dictionary<string, decimal>> MrrAsync(CancellationToken cancellationToken)
    {
        var rows = await context.SubscriptionItems.AsNoTracking()
            .Where(item => item.Quantity != null)
            .Join(
                context.Subscriptions.Where(subscription => subscription.Status == SubscriptionStatus.Active || subscription.Status == SubscriptionStatus.PastDue),
                item => item.SubscriptionId,
                subscription => subscription.Id,
                (item, subscription) => new { subscription.Currency, subscription.DiscountId, item.PriceId, Quantity = item.Quantity!.Value })
            .GroupBy(row => new { row.Currency, row.DiscountId, row.PriceId, row.Quantity })
            .Select(group => new { group.Key.Currency, group.Key.DiscountId, group.Key.PriceId, group.Key.Quantity, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var priceIds = rows.Select(row => row.PriceId).Distinct().ToList();
        var prices = await context.Prices.AsNoTracking().Where(price => priceIds.Contains(price.Id)).ToDictionaryAsync(price => price.Id, cancellationToken);
        var rateIds = prices.Values.Where(price => price.TaxRateId is not null).Select(price => price.TaxRateId!.Value).Distinct().ToList();
        var rates = await context.TaxRates.AsNoTracking().Where(rate => rateIds.Contains(rate.Id))
            .ToDictionaryAsync(rate => rate.Id, rate => new TaxRateSnapshot(rate.Percentage, rate.IsInclusive), cancellationToken);
        var discountIds = rows.Where(row => row.DiscountId is not null).Select(row => row.DiscountId!.Value).Distinct().ToList();
        var discounts = await context.Discounts.AsNoTracking()
            .Where(discount => discountIds.Contains(discount.Id) && discount.EndedAt == null)
            .Join(context.Coupons, discount => discount.CouponId, coupon => coupon.Id, (discount, coupon) => new { discount.Id, Coupon = coupon })
            .ToDictionaryAsync(row => row.Id, row => new RecurringDiscount(row.Coupon.PercentOff, row.Coupon.AmountOff, row.Coupon.Currency, row.Coupon.ProductIds), cancellationToken);

        RecurringItem Item(Guid priceId, int quantity) =>
            new(prices[priceId], quantity, prices[priceId].TaxRateId is { } rateId ? rates[rateId] : null);

        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var subscription in rows.GroupBy(row => (row.Currency, Discount: row.DiscountId is { } id && discounts.ContainsKey(id) ? id : (Guid?)null)))
        {
            var monthly = subscription.Key.Discount is { } discountId
                ? RecurringRevenue.Monthly(subscription.Key.Currency, [.. subscription.Select(row => Item(row.PriceId, row.Quantity))], discounts[discountId])
                : subscription.Sum(row => row.Count * RecurringRevenue.Monthly(subscription.Key.Currency, [Item(row.PriceId, row.Quantity)], null));
            totals[subscription.Key.Currency] = totals.GetValueOrDefault(subscription.Key.Currency) + monthly;
        }

        return totals;
    }
}
