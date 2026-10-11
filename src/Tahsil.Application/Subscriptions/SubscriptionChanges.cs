using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;
using ProrationBehavior = Tahsil.Contracts.ProrationBehavior;

namespace Tahsil.Application.Subscriptions;

/// <summary>What a change of items does now: its lines for <see cref="Period"/>, invoiced at once or left for the next renewal.</summary>
public sealed record ItemChangePlan(IReadOnlyList<InvoiceLineDraft> Lines, BillingPeriod Period, bool Invoiced);

/// <summary>Changes of a subscription's items (SUB-18 to SUB-23, SUB-34), applied or previewed the same way.</summary>
public sealed class SubscriptionChanges(
    ITahsilDbContext context, SubscriptionLines lines, InvoiceFinalizer finalizer, IOptions<BillingOptions> billing, IOptions<SubscriptionOptions> options)
{
    /// <summary>
    /// The lines of a change now (SUB-20 to SUB-22): none during a trial; with a new interval, credits for the unused
    /// time of the old items (unless <c>none</c>) and the new items' full new period, invoiced at once; otherwise the
    /// proration lines of MNY-30, invoiced or pending as <paramref name="behavior"/> says.
    /// </summary>
    public async Task<ItemChangePlan> PlanAsync(
        Subscription subscription, IReadOnlyList<SubscriptionItemSpec> after, ProrationBehavior behavior, string locale, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = new BillingPeriod(subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd);
        if (subscription.Status == SubscriptionStatus.Trialing || after.Count == 0)
        {
            return new ItemChangePlan([], current, Invoiced: false);
        }

        var before = await lines.PricedAsync(subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken);
        var price = after[0].Price;
        if (price.Interval != subscription.Interval || price.IntervalCount != subscription.IntervalCount)
        {
            var period = BillingPeriods.Period(now, price.Interval!.Value, price.IntervalCount!.Value, 0, billing.Value.Zone);
            var credits = behavior == ProrationBehavior.None ? [] : await lines.ProrationAsync(before, after, current, now, creditsOnly: true, locale, cancellationToken);
            var charges = await lines.LicensedAsync(after, period, period, locale, cancellationToken);
            return new ItemChangePlan([.. credits, .. charges], period, Invoiced: true);
        }

        var prorations = behavior == ProrationBehavior.None ? [] : await lines.ProrationAsync(before, after, current, now, creditsOnly: false, locale, cancellationToken);
        return new ItemChangePlan(prorations, new BillingPeriod(now < current.Start ? current.Start : now, current.End), behavior == ProrationBehavior.AlwaysInvoice);
    }

    /// <summary>
    /// Replaces the items now and bills the change: a finalized <c>subscription_update</c> invoice, whose negative
    /// total becomes the customer's credit (SUB-23), or pending proration items. Returns the invoice, which the caller
    /// collects. Runs in the caller's transaction, which holds the subscription's lock.
    /// </summary>
    public async Task<Result<Invoice?>> ApplyNowAsync(
        Subscription subscription, Customer customer, IReadOnlyList<SubscriptionItemSpec> after, ProrationBehavior behavior, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(subscription, after, behavior, customer.Locale, now, cancellationToken);
        var replaced = subscription.ReplaceItems(after, billing.Value.Zone, now);
        if (replaced.IsFailure)
        {
            return replaced.Error;
        }

        if (plan.Lines.Count == 0)
        {
            return Result.Success<Invoice?>(null);
        }

        if (!plan.Invoiced)
        {
            foreach (var line in plan.Lines)
            {
                context.PendingInvoiceItems.Add(PendingInvoiceItem.ForProration(
                    customer.Id, subscription.Id, subscription.Currency, line.Description, line.Amount, line.TaxRateId, line.PeriodStart!.Value, line.PeriodEnd!.Value, now));
            }

            return Result.Success<Invoice?>(null);
        }

        var invoice = Invoice.CreateDraft(
            customer.Id,
            subscription.Currency,
            BillingReason.SubscriptionUpdate,
            subscription.CollectionMethod,
            subscription.DaysUntilDue,
            memo: null,
            autoAdvance: false,
            Metadata.Empty,
            now,
            subscription.Id,
            plan.Period).Value;
        invoice.AddLines(plan.Lines, customer.IsTaxExempt, now);
        context.Invoices.Add(invoice);
        subscription.SetLatestInvoice(invoice.Id, now);
        if (await finalizer.FinalizeAsync(invoice, cancellationToken) is { IsFailure: true } failed)
        {
            return failed.Error;
        }

        return invoice;
    }

    /// <summary>
    /// Whether a change made by the merchant must wait for the notice of SUB-34: it raises an individual customer's
    /// recurring amount (per day, so interval changes compare too) and takes effect before the notice period ends.
    /// </summary>
    public async Task<bool> NeedsNoticeAsync(
        Subscription subscription, Customer customer, IReadOnlyList<SubscriptionItemSpec> after, DateTimeOffset effectiveAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (customer.Type != CustomerType.Individual || effectiveAt >= now + options.Value.PriceIncreaseNotice)
        {
            return false;
        }

        var before = await lines.PricedAsync(subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken);
        return PerDay(after, now) > PerDay(before, now);
    }

    private decimal PerDay(IReadOnlyList<SubscriptionItemSpec> items, DateTimeOffset now) =>
        items.Where(item => item.Price.UsageType != UsageType.Metered && item.Quantity is > 0).Sum(item =>
            PriceAmounts.For(item.Price, item.Quantity!.Value)
            / (decimal)BillingPeriods.Period(now, item.Price.Interval!.Value, item.Price.IntervalCount!.Value, 0, billing.Value.Zone).Length.TotalDays);
}
